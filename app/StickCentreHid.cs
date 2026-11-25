using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace JesusKpekpes;

static class StickGate
{
    static int _busy;
    public static bool Enter() => Interlocked.CompareExchange(ref _busy, 1, 0) == 0;
    public static void Leave() => Volatile.Write(ref _busy, 0);
}

readonly record struct FoundPad(string Path, StickKind Kind);

/// <summary>
/// Opens the real pad only for a test or an in-memory write, shared so DS4Windows keeps its handle.
/// No read stays pending after the call returns.
/// </summary>
static class StickLink
{
    const int RateOpenMs = 20;
    static IntPtr _notify;
    static IntPtr _notifyWindow;

    public static void Listen(IntPtr window)
    {
        if (window == IntPtr.Zero) return;
        if (_notify != IntPtr.Zero && _notifyWindow == window) return;
        Silence();
        var filter = new DEV_BROADCAST_DEVICEINTERFACE
        {
            dbcc_size = Marshal.SizeOf<DEV_BROADCAST_DEVICEINTERFACE>(),
            dbcc_devicetype = 5,
            dbcc_classguid = HidGuid,
        };
        _notify = RegisterDeviceNotification(window, ref filter, 0);
        _notifyWindow = window;
    }

    public static StickOutcome Run(StickJob job)
    {
        var found = Find(out var error);
        if (found is null) return new StickOutcome { Text = error ?? "No DualSense on USB." };
        HidStick? hid = null;
        try
        {
            hid = HidStick.Open(found.Value.Path, out var openError);
            if (hid is null) return new StickOutcome { Text = openError };
            var year = DateTime.Now.Year;
            var store = StickStore.Default;
            return job switch
            {
                StickJob.Test => StickCentre.Test(hid, found.Value.Kind),
                StickJob.Apply => StickCentre.Apply(hid, store, found.Value.Kind, year),
                StickJob.Reapply => StickCentre.Reapply(hid, store, found.Value.Kind, year),
                _ => StickCentre.Restore(hid, store, found.Value.Kind, year),
            };
        }
        catch (Exception ex)
        {
            return new StickOutcome { Text = "The pad read stopped: " + ex.Message };
        }
        finally
        {
            hid?.Dispose();
        }
    }

    public static void Silence()
    {
        if (_notify == IntPtr.Zero) return;
        UnregisterDeviceNotification(_notify);
        _notify = IntPtr.Zero;
        _notifyWindow = IntPtr.Zero;
    }

    public static bool Arrival(int message, IntPtr wparam, IntPtr lparam)
    {
        if (message != 0x0219 || wparam != (IntPtr)0x8000 || lparam == IntPtr.Zero) return false;
        try
        {
            if (Marshal.ReadInt32(lparam, 4) != 5) return false;
            var name = Marshal.PtrToStringUni(IntPtr.Add(lparam, 28));
            return StickCentre.KindOf(name) is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>One short re-apply on the device-arrival message. No timer and no thread while idle.</summary>
    public static void ReapplyStoredAsync(Action<StickOutcome>? done = null)
    {
        if (!StickStore.Default.HasCorrection) return;
        if (!StickGate.Enter()) return;
        Task.Run(() =>
        {
            StickOutcome outcome;
            try { outcome = Run(StickJob.Reapply); }
            catch (Exception ex) { outcome = new StickOutcome { Text = ex.Message }; }
            finally { StickGate.Leave(); }
            if (done is not null)
            {
                try { done(outcome); } catch (Exception) { /* the window may already be gone */ }
            }
        });
    }

    public static FoundPad? Find(out string? error)
    {
        error = null;
        var sawBluetooth = false;
        FoundPad? edge = null;
        FoundPad? plain = null;
        var list = SetupDiGetClassDevs(HidGuid, null, IntPtr.Zero, 0x12);
        if (list == IntPtr.Zero || list == new IntPtr(-1))
        {
            error = "USB HID devices could not be listed.";
            return null;
        }
        try
        {
            var data = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            for (var index = 0; SetupDiEnumDeviceInterfaces(list, IntPtr.Zero, HidGuid, index, ref data); index++)
            {
                var detail = new SP_DEVICE_INTERFACE_DETAIL_DATA { cbSize = IntPtr.Size == 8 ? 8 : 6 };
                var needed = 0;
                if (!SetupDiGetDeviceInterfaceDetail(list, ref data, ref detail, Marshal.SizeOf(detail), ref needed, IntPtr.Zero))
                    continue;
                var path = detail.DevicePath ?? "";
                if (StickCentre.BusOf(path) == StickBus.Bluetooth && path.Contains("054c", StringComparison.OrdinalIgnoreCase)
                    && (path.Contains("0ce6", StringComparison.OrdinalIgnoreCase) || path.Contains("0df2", StringComparison.OrdinalIgnoreCase)))
                    sawBluetooth = true;
                var kind = StickCentre.KindOf(path);
                if (kind == StickKind.Edge && edge is null) edge = new FoundPad(path, StickKind.Edge);
                if (kind == StickKind.DualSense && plain is null) plain = new FoundPad(path, StickKind.DualSense);
                data = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(list);
        }
        if (edge is not null) return edge;
        if (plain is not null) return plain;
        error = sawBluetooth ? StickCentre.BluetoothRefusal : MissingText();
        return null;
    }

    static string MissingText()
    {
        if (HidHideBlocking()) return StickCentre.HidHideAdvice;
        return "No DualSense on USB. Plug it in with a data cable (VID 054C, PID 0CE6 or 0DF2). Bluetooth is refused.";
    }

    public static bool HidHideBlocking()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\HidHide\Parameters");
            if (key is null) return false;
            if (key.GetValue("Active") is not int active || active == 0) return false;
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return true;
            if (key.GetValue("Whitelist") is string[] list)
            {
                foreach (var line in list)
                    if (string.Equals(line, exe, StringComparison.OrdinalIgnoreCase)) return false;
            }
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    static readonly Guid HidGuid = new("4d1e55b2-f16f-11cf-88cb-001111000030");

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVICE_INTERFACE_DATA
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SP_DEVICE_INTERFACE_DETAIL_DATA
    {
        public int cbSize;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 512)]
        public string? DevicePath;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DEV_BROADCAST_DEVICEINTERFACE
    {
        public int dbcc_size;
        public int dbcc_devicetype;
        public int dbcc_reserved;
        public Guid dbcc_classguid;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr SetupDiGetClassDevs(Guid classGuid, string? enumerator, IntPtr hwndParent, int flags);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData, Guid interfaceClassGuid, int memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData, ref SP_DEVICE_INTERFACE_DETAIL_DATA deviceInterfaceDetailData, int deviceInterfaceDetailDataSize, ref int requiredSize, IntPtr deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr RegisterDeviceNotification(IntPtr recipient, ref DEV_BROADCAST_DEVICEINTERFACE filter, int flags);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool UnregisterDeviceNotification(IntPtr handle);
}

sealed class HidStick : IStickIo, IDisposable
{
    const int ErrorIoPending = 997;
    const int IoctlGetFeature = 0x000B0192;
    const int IoctlSetFeature = 0x000B0191;
    const int GenericRead = unchecked((int)0x80000000);
    const int GenericWrite = 0x40000000;
    const int FeatureTimeoutMs = 250;
    readonly IntPtr _handle;
    readonly IntPtr _event;
    bool _closed;

    HidStick(IntPtr handle, IntPtr wait)
    {
        _handle = handle;
        _event = wait;
    }

    public long Ticks => System.Diagnostics.Stopwatch.GetTimestamp();

    public static HidStick? Open(string path, out string error)
    {
        error = "";
        var handle = CreateFile(path, GenericRead | GenericWrite, 0x3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
        {
            var code = Marshal.GetLastWin32Error();
            error = code is 5 or 32
                ? "The USB pad is there but the open was refused (Windows error " + code + "). " + StickCentre.HidHideAdvice + " If DS4Windows opened it without sharing, turn its exclusive mode off. This app does not close DS4Windows."
                : "The USB pad could not be opened (Windows error " + code + "). Nothing was written.";
            return null;
        }
        var wait = CreateEvent(IntPtr.Zero, true, false, null);
        if (wait == IntPtr.Zero)
        {
            CloseHandle(handle);
            error = "The read event could not be created. Nothing was written.";
            return null;
        }
        return new HidStick(handle, wait);
    }

    public byte[]? ReadInput()
    {
        if (_closed) return null;
        var buffer = new byte[StickCentre.ReportBytes];
        ResetEvent(_event);
        var overlapped = new NativeOverlapped { EventHandle = _event };
        var started = ReadFile(_handle, buffer, buffer.Length, out _, ref overlapped);
        if (!started && Marshal.GetLastWin32Error() != ErrorIoPending)
            return null;
        if (WaitForSingleObject(_event, RateOpenMs) != 0)
        {
            CancelIoEx(_handle, ref overlapped);
            WaitForSingleObject(_event, 50);
            return null;
        }
        if (!GetOverlappedResult(_handle, ref overlapped, out var read, false) || read < 1) return null;
        if (read != buffer.Length) Array.Resize(ref buffer, read);
        return buffer;
    }

    public byte[] GetFeature(byte reportId)
    {
        if (reportId != 0x81 && reportId != 0x20) throw new InvalidOperationException("feature report refused");
        var buffer = new byte[StickCentre.ReportBytes];
        buffer[0] = reportId;
        if (!Feature(IoctlGetFeature, buffer, buffer, FeatureTimeoutMs)) return [];
        return buffer;
    }

    public void SetFeature(byte[] report)
    {
        var refused = StickCentre.ForbidReason(report);
        if (refused is not null) throw new InvalidOperationException(refused);
        if (!Feature(IoctlSetFeature, report, null, FeatureTimeoutMs))
            throw new IOException("The in-memory report was not accepted (Windows error " + Marshal.GetLastWin32Error() + ").");
    }

    public void Wait(int milliseconds) => Thread.Sleep(milliseconds);

    bool Feature(int code, byte[] input, byte[]? output, int timeoutMs)
    {
        ResetEvent(_event);
        var overlapped = new NativeOverlapped { EventHandle = _event };
        var started = output is null
            ? DeviceIoControl(_handle, code, input, input.Length, IntPtr.Zero, 0, out _, ref overlapped)
            : DeviceIoControl(_handle, code, input, input.Length, output, output.Length, out _, ref overlapped);
        if (!started && Marshal.GetLastWin32Error() != ErrorIoPending) return false;
        if (WaitForSingleObject(_event, timeoutMs) != 0)
        {
            CancelIoEx(_handle, ref overlapped);
            WaitForSingleObject(_event, 50);
            return false;
        }
        return GetOverlappedResult(_handle, ref overlapped, out _, false);
    }

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        CancelIoEx(_handle, IntPtr.Zero);
        CloseHandle(_handle);
        CloseHandle(_event);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateFile(string name, int access, int share, IntPtr security, int disposition, int flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool ReadFile(IntPtr handle, byte[] buffer, int count, out int read, ref NativeOverlapped overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(IntPtr handle, int code, byte[] input, int inputSize, byte[]? output, int outputSize, out int returned, ref NativeOverlapped overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(IntPtr handle, int code, byte[] input, int inputSize, IntPtr output, int outputSize, out int returned, ref NativeOverlapped overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetOverlappedResult(IntPtr handle, ref NativeOverlapped overlapped, out int transferred, bool wait);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CancelIoEx(IntPtr handle, ref NativeOverlapped overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CancelIoEx(IntPtr handle, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr CreateEvent(IntPtr security, bool manual, bool initial, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool ResetEvent(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern int WaitForSingleObject(IntPtr handle, int milliseconds);
}
