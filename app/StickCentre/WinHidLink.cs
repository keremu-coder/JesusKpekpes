using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace JesusKpekpes;

// Work package A: IControllerLink over Windows HID (hid.dll, setupapi.dll, kernel32.dll).
//
// Input-path rules this file keeps (docs/STICK_CENTRE.md, hard rules 1, 7, 8, 9):
//  - Nothing starts by itself. A handle exists only between Open() and Dispose(), which the caller does for the length of one
//    test or one apply. There is no thread, timer, callback or device watcher in this file.
//  - Finding the pad only asks Windows for each device's vendor and product number through a handle opened with NO access
//    rights, then opens the matching one for read and write with SHARED access, so DS4Windows and the game keep their own handle.
//  - Opening the link sends no feature report. Feature reports go out only when the caller asks.
//  - No driver, no injection, no touching any other process. HidHide is detected and explained, never changed.
//  - Only DualSense (054C:0CE6) and DualSense Edge (054C:0DF2). Nothing else is opened for read or write.
internal sealed class WinHidLink : IControllerLink
{
    private readonly SafeFileHandle _handle;
    private readonly int _inputLength;
    private readonly ManualResetEvent _readDone = new(false);
    private bool _disposed;

    public string Model { get; }
    public bool IsUsb { get; }

    private WinHidLink(SafeFileHandle handle, string model, bool usb, int inputLength)
    {
        _handle = handle;
        Model = model;
        IsUsb = usb;
        _inputLength = inputLength;
    }

    // Registers this link as the one the Stick centre tab opens. It runs once when the app loads; it only stores a delegate and
    // touches no hardware, so it adds nothing to the input path.
    [ModuleInitializer]
    internal static void Register()
    {
        if (StickCentreForm.OpenController is null) StickCentreForm.OpenController = Open;
    }

    /// <summary>
    /// Opens the first supported pad (a USB one is preferred). Returns null when no supported pad is attached.
    /// Throws <see cref="StickCentreLinkException"/> with a plain-language reason when a pad is there but cannot be opened.
    /// </summary>
    internal static IControllerLink? Open()
    {
        if (!OperatingSystem.IsWindows()) return null;

        var found = new List<HidRules.Candidate>();
        var blocked = new List<(string Path, int Error)>();
        foreach (var path in EnumerateHidPaths())
        {
            // No access rights at all: Windows answers the question "who are you" without taking part in any traffic.
            using var probe = Native.CreateFile(path, 0, Native.FileShareReadWrite, IntPtr.Zero, Native.OpenExisting, 0, IntPtr.Zero);
            if (probe.IsInvalid)
            {
                blocked.Add((path, Marshal.GetLastWin32Error()));
                continue;
            }
            var attributes = new Native.HiddAttributes { Size = Marshal.SizeOf<Native.HiddAttributes>() };
            if (!Native.HidD_GetAttributes(probe, ref attributes)) continue;
            var model = HidRules.ModelFor(attributes.VendorId, attributes.ProductId);
            if (model is null) continue;
            var inputLength = ReadInputLength(probe);
            found.Add(new HidRules.Candidate(path, model, HidRules.LooksUsb(path, inputLength), inputLength));
        }

        var pick = HidRules.Prefer(found);
        if (pick is null)
        {
            // Nothing supported answered. If something refused to answer and HidHide is installed, that is the likely reason.
            if (blocked.Count > 0 && HidRules.HidHideInstalled())
                throw new StickCentreLinkException(HidRules.Explain(Native.ErrorAccessDenied, hidHide: true));
            return null;
        }

        var handle = Native.CreateFile(pick.Path, Native.GenericRead | Native.GenericWrite, Native.FileShareReadWrite, IntPtr.Zero,
            Native.OpenExisting, Native.FileFlagOverlapped, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new StickCentreLinkException(HidRules.Explain(error, HidRules.HidHideInstalled()));
        }
        return new WinHidLink(handle, pick.Model, pick.Usb, pick.InputLength);
    }

    // One input report, or null if none arrives within timeoutMs. Overlapped, so the wait is bounded and a stuck read is
    // cancelled instead of left pending. Byte 0 of the answer is the report id.
    public byte[]? ReadInputReport(int timeoutMs)
    {
        if (_disposed || _inputLength <= 0) return null;
        var buffer = new byte[_inputLength];
        _readDone.Reset();
        var overlapped = new NativeOverlapped { EventHandle = _readDone.SafeWaitHandle.DangerousGetHandle() };
        var pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            var started = Native.ReadFile(_handle, pinned.AddrOfPinnedObject(), buffer.Length, out var read, ref overlapped);
            if (!started)
            {
                var error = Marshal.GetLastWin32Error();
                if (error != Native.ErrorIoPending) return null;
                if (!_readDone.WaitOne(Math.Max(0, timeoutMs)))
                {
                    Native.CancelIoEx(_handle, ref overlapped);
                    // Wait for the cancel to finish so the buffer is never written after it is released.
                    Native.GetOverlappedResult(_handle, ref overlapped, out _, true);
                    return null;
                }
                if (!Native.GetOverlappedResult(_handle, ref overlapped, out read, false)) return null;
            }
            if (read <= 0) return null;
            var report = new byte[read];
            Array.Copy(buffer, report, read);
            return report;
        }
        finally
        {
            pinned.Free();
        }
    }

    public byte[]? GetFeatureReport(byte reportId, int length)
    {
        if (_disposed || length <= 0) return null;
        var buffer = new byte[length];
        buffer[0] = reportId;
        return Native.HidD_GetFeature(_handle, buffer, buffer.Length) ? buffer : null;
    }

    public bool SendFeatureReport(byte[] data)
    {
        if (_disposed || data is null || data.Length == 0) return false;
        return Native.HidD_SetFeature(_handle, data, data.Length);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _handle.Dispose();
        _readDone.Dispose();
    }

    private static IEnumerable<string> EnumerateHidPaths()
    {
        Native.HidD_GetHidGuid(out var guid);
        var set = Native.SetupDiGetClassDevs(ref guid, null, IntPtr.Zero, Native.DigcfPresent | Native.DigcfDeviceInterface);
        if (set == Native.InvalidHandle) yield break;
        try
        {
            for (uint index = 0; ; index++)
            {
                var data = new Native.DeviceInterfaceData { Size = Marshal.SizeOf<Native.DeviceInterfaceData>() };
                if (!Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, index, ref data)) yield break;
                var path = DevicePath(set, ref data);
                if (path is not null) yield return path;
            }
        }
        finally
        {
            Native.SetupDiDestroyDeviceInfoList(set);
        }
    }

    private static string? DevicePath(IntPtr set, ref Native.DeviceInterfaceData data)
    {
        Native.SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out var needed, IntPtr.Zero);
        if (needed == 0) return null;
        var memory = Marshal.AllocHGlobal((int)needed);
        try
        {
            // cbSize is 8 on 64-bit Windows and 6 on 32-bit; the path starts 4 bytes in.
            Marshal.WriteInt32(memory, IntPtr.Size == 8 ? 8 : 6);
            if (!Native.SetupDiGetDeviceInterfaceDetail(set, ref data, memory, needed, out _, IntPtr.Zero)) return null;
            return Marshal.PtrToStringUni(memory + 4);
        }
        finally
        {
            Marshal.FreeHGlobal(memory);
        }
    }

    private static int ReadInputLength(SafeFileHandle probe)
    {
        if (!Native.HidD_GetPreparsedData(probe, out var preparsed)) return 0;
        try
        {
            return Native.HidP_GetCaps(preparsed, out var caps) == Native.HidpStatusSuccess ? caps.InputReportByteLength : 0;
        }
        finally
        {
            Native.HidD_FreePreparsedData(preparsed);
        }
    }

    private static class Native
    {
        internal const uint GenericRead = 0x80000000;
        internal const uint GenericWrite = 0x40000000;
        internal const uint FileShareReadWrite = 3;
        internal const uint OpenExisting = 3;
        internal const uint FileFlagOverlapped = 0x40000000;
        internal const uint DigcfPresent = 2;
        internal const uint DigcfDeviceInterface = 0x10;
        internal const int ErrorAccessDenied = 5;
        internal const int ErrorIoPending = 997;
        internal const int HidpStatusSuccess = 0x110000;
        internal static readonly IntPtr InvalidHandle = new(-1);

        [StructLayout(LayoutKind.Sequential)]
        internal struct HiddAttributes
        {
            public int Size;
            public ushort VendorId;
            public ushort ProductId;
            public ushort VersionNumber;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct DeviceInterfaceData
        {
            public int Size;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct HidpCaps
        {
            public ushort Usage;
            public ushort UsagePage;
            public ushort InputReportByteLength;
            public ushort OutputReportByteLength;
            public ushort FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes;
            public ushort NumberInputButtonCaps;
            public ushort NumberInputValueCaps;
            public ushort NumberInputDataIndices;
            public ushort NumberOutputButtonCaps;
            public ushort NumberOutputValueCaps;
            public ushort NumberOutputDataIndices;
            public ushort NumberFeatureButtonCaps;
            public ushort NumberFeatureValueCaps;
            public ushort NumberFeatureDataIndices;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool ReadFile(SafeFileHandle file, IntPtr buffer, int bytes, out int read, ref NativeOverlapped overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool GetOverlappedResult(SafeFileHandle file, ref NativeOverlapped overlapped, out int transferred, bool wait);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool CancelIoEx(SafeFileHandle file, ref NativeOverlapped overlapped);

        [DllImport("hid.dll")]
        internal static extern void HidD_GetHidGuid(out Guid guid);

        [DllImport("hid.dll", SetLastError = true)]
        internal static extern bool HidD_GetAttributes(SafeFileHandle file, ref HiddAttributes attributes);

        [DllImport("hid.dll", SetLastError = true)]
        internal static extern bool HidD_GetPreparsedData(SafeFileHandle file, out IntPtr preparsed);

        [DllImport("hid.dll", SetLastError = true)]
        internal static extern bool HidD_FreePreparsedData(IntPtr preparsed);

        [DllImport("hid.dll")]
        internal static extern int HidP_GetCaps(IntPtr preparsed, out HidpCaps caps);

        [DllImport("hid.dll", SetLastError = true)]
        internal static extern bool HidD_GetFeature(SafeFileHandle file, byte[] buffer, int length);

        [DllImport("hid.dll", SetLastError = true)]
        internal static extern bool HidD_SetFeature(SafeFileHandle file, byte[] buffer, int length);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, string? enumerator, IntPtr parent, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        internal static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid classGuid, uint index, ref DeviceInterfaceData data);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref DeviceInterfaceData data, IntPtr detail, uint size, out uint needed, IntPtr info);

        [DllImport("setupapi.dll", SetLastError = true)]
        internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    }
}
