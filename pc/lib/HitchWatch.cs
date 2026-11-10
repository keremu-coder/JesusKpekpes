// Names the driver or process behind the longest stall. Changes nothing.
// A private system logger is used (mode 0x02000000). The shared kernel logger is not started,
// so another watch keeps running.
//
// Payload layouts are the ones KernelTraceEventParser reads, not a guess:
//   DPC / threaded DPC / timer DPC (opcodes 68, 66, 69) and ISR (opcodes 67 and 50):
//     bytes 0..7  start time, in the same clock as the event time
//     bytes 8..15 routine address on 64-bit Windows
//   Context switch (opcode 36, thread task): new thread id at 0, new priority at byte 8, length 24
//   Ready thread (opcode 50, thread task guid, not the perf task): woken thread id at 0, length 8
// Duration is event time minus start time. Both are the performance counter when the logger asks for it.
// A routine is named from the loaded-driver list (image base and size), because a driver that is already
// loaded does not announce itself again during a short watch.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Kpekpes
{
    public sealed class KpekpesHitchHit
    {
        public string Name;
        public string Kind;
        public long Us;
    }

    public sealed class KpekpesHitchResult
    {
        public bool Ok;
        public string Error;
        public int Dropped;
        public int Seen;
        public int BadTime;
        public KpekpesHitchHit[] Hits;
    }

    public static class KpekpesHitchMath
    {
        public const long MaxDriverUs = 2000000;
        public const long MaxWakeUs = 50000;
        public const int MinWakePriority = 8;
        public const int ModuleStride = 296;

        public static readonly Guid PerfGuid = new Guid("ce1dbfb4-137e-4da6-87b0-3f59aa102cbc");
        public static readonly Guid ThreadGuid = new Guid("3d6fa8d1-fe05-11d0-9dda-00c04fd7ba7c");
        public static readonly Guid KernelGuid = new Guid("9e814aad-3204-11d2-9a82-006008a86939");

        public static long DurationUs(long start, long end, long freq)
        {
            if (freq <= 0 || end < start) return -1;
            if (end == start) return 0;
            double us = ((double)(end - start) * 1000000.0) / (double)freq;
            if (us >= 2000000000.0) return -1;
            if (us < 1.0) return 0;
            return (long)us;
        }

        // 1 = usable, 2 = the clock values cannot be trusted, 0 = ignore
        public static int ClassifyStall(byte[] data, int length, long endQpc, long freq, long maxUs, out long us, out ulong routine)
        {
            us = -1;
            routine = 0;
            if (data == null || length < 16 || data.Length < length) return 0;
            routine = (ulong)BitConverter.ToInt64(data, 8);
            if (routine == 0) return 0;
            long start = BitConverter.ToInt64(data, 0);
            us = DurationUs(start, endQpc, freq);
            if (us < 0 || us > maxUs) return 2;
            if (us == 0) return 0;
            return 1;
        }

        public static string KindOf(Guid provider, ushort task, byte opcode)
        {
            bool perf = provider == PerfGuid || task == 11;
            if (perf && provider != ThreadGuid && (opcode == 66 || opcode == 68 || opcode == 69)) return "DPC";
            if (perf && provider != ThreadGuid && (opcode == 67 || opcode == 50)) return "ISR";
            if (opcode == 36 && provider != PerfGuid && task != 11 && (provider == ThreadGuid || task == 2)) return "CSwitch";
            if (opcode == 50 && provider == ThreadGuid && task != 11) return "Wake";
            return "";
        }

        public static KpekpesModuleMap ParseModules(byte[] blob)
        {
            var found = new List<KpekpesModRow>();
            if (blob == null || blob.Length < 8 + ModuleStride) return KpekpesModuleMap.FromRows(found);
            int count = BitConverter.ToInt32(blob, 0);
            if (count <= 0 || count > 100000) return KpekpesModuleMap.FromRows(found);
            long need = 8L + ((long)count * ModuleStride);
            if (need > blob.Length) return KpekpesModuleMap.FromRows(found);
            int i;
            for (i = 0; i < count; i++)
            {
                int at = 8 + (i * ModuleStride);
                ulong imageBase = (ulong)BitConverter.ToInt64(blob, at + 16);
                uint imageSize = (uint)BitConverter.ToInt32(blob, at + 24);
                int nameAt = BitConverter.ToUInt16(blob, at + 38);
                if (imageBase == 0 || imageSize == 0) continue;
                if (nameAt < 0 || nameAt >= 256) nameAt = 0;
                string name = FileName(blob, at + 40, nameAt);
                if (name.Length == 0) continue;
                var row = new KpekpesModRow();
                row.Base = imageBase;
                row.Size = imageSize;
                row.Name = name;
                found.Add(row);
            }
            found.Sort(delegate(KpekpesModRow a, KpekpesModRow b)
            {
                if (a.Base < b.Base) return -1;
                if (a.Base > b.Base) return 1;
                return 0;
            });
            return KpekpesModuleMap.FromRows(found);
        }

        static string FileName(byte[] blob, int pathAt, int nameAt)
        {
            int start = pathAt + nameAt;
            if (start < 0 || start >= blob.Length) return "";
            int end = start;
            int limit = pathAt + 256;
            if (limit > blob.Length) limit = blob.Length;
            while (end < limit && blob[end] != 0) end++;
            if (end == start) return "";
            var sb = new StringBuilder();
            int i;
            for (i = start; i < end; i++)
            {
                byte c = blob[i];
                if (c < 32) break;
                sb.Append((char)c);
            }
            string s = sb.ToString();
            int slash = s.LastIndexOf('\\');
            if (slash >= 0 && slash < s.Length - 1) s = s.Substring(slash + 1);
            return s.Trim();
        }
    }

    public sealed class KpekpesModRow
    {
        public ulong Base;
        public uint Size;
        public string Name;
    }

    public sealed class KpekpesModuleMap
    {
        public readonly ulong[] Bases;
        public readonly uint[] Sizes;
        public readonly string[] Names;

        public KpekpesModuleMap(ulong[] bases, uint[] sizes, string[] names)
        {
            Bases = bases;
            Sizes = sizes;
            Names = names;
        }

        public static KpekpesModuleMap FromRows(List<KpekpesModRow> rows)
        {
            int n = rows.Count;
            var bases = new ulong[n];
            var sizes = new uint[n];
            var names = new string[n];
            int i;
            for (i = 0; i < n; i++)
            {
                bases[i] = rows[i].Base;
                sizes[i] = rows[i].Size;
                names[i] = rows[i].Name;
            }
            return new KpekpesModuleMap(bases, sizes, names);
        }

        public string Find(ulong address)
        {
            if (Bases == null || Bases.Length == 0 || address == 0) return "";
            int lo = 0;
            int hi = Bases.Length - 1;
            int found = -1;
            while (lo <= hi)
            {
                int mid = lo + ((hi - lo) / 2);
                if (Bases[mid] <= address)
                {
                    found = mid;
                    lo = mid + 1;
                }
                else hi = mid - 1;
            }
            int left = found;
            int guard = 0;
            while (left >= 0 && guard < 8)
            {
                if (address >= Bases[left])
                {
                    ulong end = Bases[left] + (ulong)Sizes[left];
                    if (address < end)
                    {
                        string name = Names[left];
                        if (name == null) return "";
                        return name;
                    }
                }
                left--;
                guard++;
            }
            return "";
        }
    }

    public sealed class KpekpesWakeTracker
    {
        readonly Dictionary<int, long> _ready = new Dictionary<int, long>();

        public void Ready(int tid, long qpc)
        {
            if (tid <= 0) return;
            if (!_ready.ContainsKey(tid)) _ready[tid] = qpc;
        }

        public long Take(int tid, int priority, long endQpc, long freq, int minPriority, long maxUs)
        {
            if (tid <= 0) return -1;
            long start;
            if (!_ready.TryGetValue(tid, out start)) return -1;
            _ready.Remove(tid);
            if (priority < minPriority) return -1;
            long us = KpekpesHitchMath.DurationUs(start, endQpc, freq);
            if (us <= 0 || us > maxUs) return -1;
            return us;
        }
    }

    public static class KpekpesHitch
    {
        const string SessionName = "Kpekpes Hitch";
        const uint SystemLoggerMode = 0x02000000;
        const uint RealTimeMode = 0x00000100;
        const uint FlagDpc = 0x00000020;
        const uint FlagIsr = 0x00000040;
        const uint FlagDispatch = 0x00000800;
        const uint FlagCswitch = 0x00000010;
        const uint ProcessEventRecord = 0x10000000;
        const uint ProcessRawTime = 0x00001000;
        const uint TracedGuid = 0x00020000;
        const int StopTrace = 1;

        public static bool LayoutOk()
        {
            if (Marshal.SizeOf(typeof(WnodeHeader)) != 48) return false;
            if (Marshal.SizeOf(typeof(EventTraceProperties)) != 120) return false;
            if (Marshal.OffsetOf(typeof(EventTraceProperties), "EnableFlags").ToInt32() != 72) return false;
            if (Marshal.OffsetOf(typeof(EventTraceProperties), "LoggerNameOffset").ToInt32() != 116) return false;
            if (Marshal.SizeOf(typeof(EventRecord)) != 112) return false;
            if (Marshal.OffsetOf(typeof(EventRecord), "UserData").ToInt32() != 96) return false;
            if (Marshal.SizeOf(typeof(EventTraceLogfile)) != 448) return false;
            if (Marshal.OffsetOf(typeof(EventTraceLogfile), "BufferCallback").ToInt32() != 400) return false;
            if (Marshal.OffsetOf(typeof(EventTraceLogfile), "EventRecordCallback").ToInt32() != 424) return false;
            if (Marshal.SizeOf(typeof(ThreadBasic)) != 48) return false;
            if (Marshal.OffsetOf(typeof(ThreadBasic), "UniqueProcess").ToInt32() != 16) return false;
            return true;
        }

        public static KpekpesHitchResult Watch(int seconds)
        {
            var result = new KpekpesHitchResult();
            result.Ok = false;
            result.Error = "";
            result.Hits = new KpekpesHitchHit[0];
            if (IntPtr.Size != 8)
            {
                result.Error = "This PowerShell is 32-bit. The hitch watch needs 64-bit Windows PowerShell.";
                return result;
            }
            if (!LayoutOk())
            {
                result.Error = "The hitch watch was not started because its trace layout does not match Windows.";
                return result;
            }
            if (seconds < 5) seconds = 5;
            if (seconds > 60) seconds = 60;
            var session = new Session();
            try
            {
                string err = session.Run(seconds);
                result.Dropped = session.Dropped;
                result.Seen = session.Seen;
                result.BadTime = session.BadTime;
                result.Hits = session.Hits();
                if (err != null && err.Length > 0 && result.Hits.Length == 0)
                {
                    result.Error = err;
                    return result;
                }
                result.Ok = true;
                return result;
            }
            catch (Exception ex)
            {
                result.Ok = false;
                result.Error = "The hitch watch stopped. " + ex.Message;
                result.Hits = new KpekpesHitchHit[0];
                return result;
            }
        }

        sealed class Session
        {
            readonly HitchEventCallback _callback;
            readonly object _gate = new object();
            readonly Dictionary<string, long> _best = new Dictionary<string, long>();
            readonly Dictionary<int, string> _proc = new Dictionary<int, string>();
            readonly KpekpesWakeTracker _wakes = new KpekpesWakeTracker();
            readonly ManualResetEvent _entered = new ManualResetEvent(false);
            KpekpesModuleMap _modules;
            long _freq;
            int _seen;
            int _badTime;
            int _dropped;
            volatile int _processRc;
            ulong _traceHandle;
            string _callbackError;

            public int Seen { get { return _seen; } }
            public int BadTime { get { return _badTime; } }
            public int Dropped { get { return _dropped; } }

            public Session()
            {
                _callback = new HitchEventCallback(OnEvent);
                _modules = KpekpesModuleMap.FromRows(new List<KpekpesModRow>());
            }

            public KpekpesHitchHit[] Hits()
            {
                lock (_gate)
                {
                    var list = new List<KpekpesHitchHit>();
                    foreach (KeyValuePair<string, long> kv in _best)
                    {
                        int bar = kv.Key.IndexOf('|');
                        if (bar <= 0) continue;
                        var hit = new KpekpesHitchHit();
                        hit.Kind = kv.Key.Substring(0, bar);
                        hit.Name = kv.Key.Substring(bar + 1);
                        hit.Us = kv.Value;
                        list.Add(hit);
                    }
                    return list.ToArray();
                }
            }

            public string Run(int seconds)
            {
                IntPtr props = IntPtr.Zero;
                IntPtr loggerName = IntPtr.Zero;
                ulong session = 0;
                ulong trace = 0;
                bool started = false;
                bool opened = false;
                bool stopped = false;
                Thread worker = null;
                try
                {
                    if (!QueryPerformanceFrequency(out _freq) || _freq <= 0)
                        return "Windows would not give a timer. The watch was not started.";
                    _modules = LoadModules();
                    props = BuildProps();
                    int rc = StartTrace(out session, SessionName, props);
                    if (rc == 183)
                    {
                        ControlTrace(0, SessionName, props, StopTrace);
                        rc = StartTrace(out session, SessionName, props);
                    }
                    if (rc != 0) return Reason(rc);
                    started = true;
                    loggerName = AllocText(SessionName);
                    var log = new EventTraceLogfile();
                    log.LoggerName = loggerName;
                    log.ProcessTraceMode = ProcessEventRecord | RealTimeMode | ProcessRawTime;
                    log.EventRecordCallback = Marshal.GetFunctionPointerForDelegate(_callback);
                    log.IsKernelTrace = 1;
                    trace = OpenTrace(ref log);
                    if (trace == 0 || trace == ulong.MaxValue)
                    {
                        int win = Marshal.GetLastWin32Error();
                        if (win == 0) win = 6;
                        return Reason(win);
                    }
                    opened = true;
                    _traceHandle = trace;
                    worker = new Thread(new ThreadStart(Consume));
                    worker.IsBackground = true;
                    worker.Start();
                    if (!_entered.WaitOne(5000))
                        return "The trace did not start listening.";
                    int left = seconds * 1000;
                    while (left > 0)
                    {
                        Thread.Sleep(200);
                        left -= 200;
                        if (!worker.IsAlive && _processRc != 0 && _processRc != 1223) break;
                    }
                    ControlTrace(session, SessionName, props, StopTrace);
                    stopped = true;
                    worker.Join(15000);
                    _dropped = ReadDropped(props);
                    if (_callbackError != null && _callbackError.Length > 0 && _seen == 0)
                        return "The hitch watch could not read the trace. " + _callbackError;
                    if (_processRc != 0 && _processRc != 1223 && _seen == 0)
                        return Reason(_processRc);
                    return null;
                }
                finally
                {
                    if (started && !stopped && props != IntPtr.Zero)
                    {
                        try { ControlTrace(session, SessionName, props, StopTrace); } catch { }
                    }
                    if (worker != null && worker.IsAlive) worker.Join(5000);
                    if (opened) CloseTrace(trace);
                    if (props != IntPtr.Zero) Marshal.FreeHGlobal(props);
                    if (loggerName != IntPtr.Zero) Marshal.FreeHGlobal(loggerName);
                }
            }

            void Consume()
            {
                _entered.Set();
                var handles = new ulong[1];
                handles[0] = _traceHandle;
                _processRc = ProcessTrace(handles, 1, IntPtr.Zero, IntPtr.Zero);
            }

            void OnEvent(IntPtr recordPtr)
            {
                try
                {
                    object boxed = Marshal.PtrToStructure(recordPtr, typeof(EventRecord));
                    var rec = (EventRecord)boxed;
                    byte op = rec.Opcode;
                    if (op == 0) op = (byte)rec.Id;
                    string kind = KpekpesHitchMath.KindOf(rec.ProviderId, rec.Task, op);
                    if (kind.Length == 0) return;
                    int len = rec.UserDataLength;
                    if (len <= 0 || len > 256 || rec.UserData == IntPtr.Zero) return;
                    byte[] data = new byte[len];
                    Marshal.Copy(rec.UserData, data, 0, len);
                    if (kind == "DPC" || kind == "ISR")
                    {
                        long us;
                        ulong routine;
                        int cls = KpekpesHitchMath.ClassifyStall(data, len, rec.TimeStamp, _freq, KpekpesHitchMath.MaxDriverUs, out us, out routine);
                        if (cls == 2)
                        {
                            lock (_gate) { _badTime++; }
                            return;
                        }
                        if (cls != 1) return;
                        string name = _modules.Find(routine);
                        lock (_gate) { _seen++; }
                        Note(kind, name, us);
                        return;
                    }
                    if (kind == "Wake")
                    {
                        if (len < 8) return;
                        int tid = BitConverter.ToInt32(data, 0);
                        _wakes.Ready(tid, rec.TimeStamp);
                        return;
                    }
                    if (kind == "CSwitch")
                    {
                        if (len < 24) return;
                        int tid = BitConverter.ToInt32(data, 0);
                        int pri = data[8];
                        long us = _wakes.Take(tid, pri, rec.TimeStamp, _freq, KpekpesHitchMath.MinWakePriority, KpekpesHitchMath.MaxWakeUs);
                        if (us <= 0) return;
                        string name = ProcessName(tid);
                        lock (_gate) { _seen++; }
                        Note("Wake", name, us);
                    }
                }
                catch (Exception ex)
                {
                    if (_callbackError == null) _callbackError = ex.Message;
                }
            }

            void Note(string kind, string name, long us)
            {
                if (name == null) return;
                name = name.Trim();
                if (name.Length == 0 || us <= 0) return;
                string key = kind + "|" + name;
                lock (_gate)
                {
                    long prev;
                    if (_best.TryGetValue(key, out prev))
                    {
                        if (us > prev) _best[key] = us;
                    }
                    else _best[key] = us;
                }
            }

            string ProcessName(int tid)
            {
                if (tid <= 0) return "";
                lock (_gate)
                {
                    string cached;
                    if (_proc.TryGetValue(tid, out cached)) return cached;
                }
                string name = LookupProcess(tid);
                lock (_gate) { _proc[tid] = name; }
                return name;
            }

            static string LookupProcess(int tid)
            {
                IntPtr thread = OpenThread(0x0840, false, tid);
                if (thread == IntPtr.Zero) return "";
                IntPtr process = IntPtr.Zero;
                try
                {
                    var info = new ThreadBasic();
                    int status = NtQueryInformationThread(thread, 0, ref info, Marshal.SizeOf(typeof(ThreadBasic)), IntPtr.Zero);
                    if (status != 0) return "";
                    long pid = info.UniqueProcess.ToInt64();
                    if (pid <= 0) return "";
                    if (pid == 4) return "System";
                    process = OpenProcess(0x1000, false, (int)pid);
                    if (process == IntPtr.Zero) return "";
                    int chars = 520;
                    var sb = new StringBuilder(chars);
                    if (!QueryFullProcessImageName(process, 0, sb, ref chars)) return "";
                    string full = sb.ToString();
                    int slash = full.LastIndexOf('\\');
                    if (slash >= 0 && slash < full.Length - 1) return full.Substring(slash + 1);
                    return full.Trim();
                }
                finally
                {
                    if (process != IntPtr.Zero) CloseHandle(process);
                    CloseHandle(thread);
                }
            }

            static KpekpesModuleMap LoadModules()
            {
                int cap = 1024 * 256;
                int guard;
                for (guard = 0; guard < 8; guard++)
                {
                    IntPtr buf = Marshal.AllocHGlobal(cap);
                    try
                    {
                        int need;
                        int status = NtQuerySystemInformation(11, buf, cap, out need);
                        if (status == 0)
                        {
                            byte[] copy = new byte[cap];
                            Marshal.Copy(buf, copy, 0, cap);
                            return KpekpesHitchMath.ParseModules(copy);
                        }
                        if (status != unchecked((int)0xC0000004)) break;
                        if (need > cap && need < 64 * 1024 * 1024) cap = need + 65536;
                        else cap = cap * 2;
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buf);
                    }
                }
                return KpekpesModuleMap.FromRows(new List<KpekpesModRow>());
            }

            static IntPtr BuildProps()
            {
                int propSize = Marshal.SizeOf(typeof(EventTraceProperties));
                byte[] name = Encoding.Unicode.GetBytes(SessionName + "\0");
                int total = propSize + name.Length;
                IntPtr mem = Marshal.AllocHGlobal(total);
                var zero = new byte[total];
                Marshal.Copy(zero, 0, mem, total);
                var props = new EventTraceProperties();
                props.Wnode.BufferSize = (uint)total;
                props.Wnode.Guid = KpekpesHitchMath.KernelGuid;
                props.Wnode.ClientContext = 1;
                props.Wnode.Flags = TracedGuid;
                props.BufferSize = 256;
                props.MinimumBuffers = 32;
                props.MaximumBuffers = 128;
                props.LogFileMode = RealTimeMode | SystemLoggerMode;
                props.FlushTimer = 1;
                props.EnableFlags = FlagDpc | FlagIsr | FlagDispatch | FlagCswitch;
                props.LoggerNameOffset = (uint)propSize;
                Marshal.StructureToPtr(props, mem, false);
                Marshal.Copy(name, 0, IntPtr.Add(mem, propSize), name.Length);
                return mem;
            }

            static int ReadDropped(IntPtr props)
            {
                int lost = Marshal.ReadInt32(props, 88);
                int realtime = Marshal.ReadInt32(props, 100);
                if (lost < 0) lost = 0;
                if (realtime < 0) realtime = 0;
                long sum = (long)lost + realtime;
                if (sum > int.MaxValue) return int.MaxValue;
                return (int)sum;
            }

            static IntPtr AllocText(string text)
            {
                byte[] raw = Encoding.Unicode.GetBytes(text + "\0");
                IntPtr p = Marshal.AllocHGlobal(raw.Length);
                Marshal.Copy(raw, 0, p, raw.Length);
                return p;
            }

            static string Reason(int code)
            {
                if (code == 5) return "Windows refused the trace because this is not running as Administrator.";
                if (code == 183) return "A hitch watch is already running.";
                if (code == 1450) return "Windows has no room for another trace. Close anything else that is watching the kernel, then try again.";
                return "Windows would not start the trace (error " + code.ToString() + ").";
            }
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate void HitchEventCallback(IntPtr record);

        [StructLayout(LayoutKind.Sequential)]
        struct WnodeHeader
        {
            public uint BufferSize;
            public uint ProviderId;
            public ulong HistoricalContext;
            public ulong TimeStamp;
            public Guid Guid;
            public uint ClientContext;
            public uint Flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct EventTraceProperties
        {
            public WnodeHeader Wnode;
            public uint BufferSize;
            public uint MinimumBuffers;
            public uint MaximumBuffers;
            public uint MaximumFileSize;
            public uint LogFileMode;
            public uint FlushTimer;
            public uint EnableFlags;
            public int AgeLimit;
            public uint NumberOfBuffers;
            public uint FreeBuffers;
            public uint EventsLost;
            public uint BuffersWritten;
            public uint LogBuffersLost;
            public uint RealTimeBuffersLost;
            public IntPtr LoggerThreadId;
            public uint LogFileNameOffset;
            public uint LoggerNameOffset;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct EventRecord
        {
            public ushort Size;
            public ushort HeaderType;
            public ushort Flags;
            public ushort EventProperty;
            public int ThreadId;
            public int ProcessId;
            public long TimeStamp;
            public Guid ProviderId;
            public ushort Id;
            public byte Version;
            public byte Channel;
            public byte Level;
            public byte Opcode;
            public ushort Task;
            public ulong Keyword;
            public uint KernelTime;
            public uint UserTime;
            public Guid ActivityId;
            public byte ProcessorNumber;
            public byte Alignment;
            public ushort LoggerId;
            public ushort ExtendedDataCount;
            public ushort UserDataLength;
            public IntPtr ExtendedData;
            public IntPtr UserData;
            public IntPtr UserContext;
        }

        [StructLayout(LayoutKind.Explicit, Size = 448)]
        struct EventTraceLogfile
        {
            [FieldOffset(0)] public IntPtr LogFileName;
            [FieldOffset(8)] public IntPtr LoggerName;
            [FieldOffset(16)] public long CurrentTime;
            [FieldOffset(24)] public uint BuffersRead;
            [FieldOffset(28)] public uint ProcessTraceMode;
            [FieldOffset(400)] public IntPtr BufferCallback;
            [FieldOffset(408)] public uint BufferSize;
            [FieldOffset(412)] public uint Filled;
            [FieldOffset(416)] public uint EventsLost;
            [FieldOffset(424)] public IntPtr EventRecordCallback;
            [FieldOffset(432)] public uint IsKernelTrace;
            [FieldOffset(440)] public IntPtr Context;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct ThreadBasic
        {
            public int ExitStatus;
            public IntPtr Teb;
            public IntPtr UniqueProcess;
            public IntPtr UniqueThread;
            public IntPtr Affinity;
            public int Priority;
            public int BasePriority;
        }

        [DllImport("kernel32.dll")]
        static extern bool QueryPerformanceFrequency(out long frequency);

        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll")]
        static extern IntPtr OpenThread(uint access, bool inherit, int threadId);

        [DllImport("kernel32.dll")]
        static extern IntPtr OpenProcess(uint access, bool inherit, int processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

        [DllImport("ntdll.dll")]
        static extern int NtQueryInformationThread(IntPtr thread, int infoClass, ref ThreadBasic info, int length, IntPtr returnLength);

        [DllImport("ntdll.dll")]
        static extern int NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int returnLength);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern int StartTrace(out ulong session, string name, IntPtr properties);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        static extern int ControlTrace(ulong session, string name, IntPtr properties, uint control);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern ulong OpenTrace(ref EventTraceLogfile logfile);

        [DllImport("advapi32.dll")]
        static extern int ProcessTrace(ulong[] handles, uint count, IntPtr start, IntPtr end);

        [DllImport("advapi32.dll")]
        static extern int CloseTrace(ulong trace);
    }
}
