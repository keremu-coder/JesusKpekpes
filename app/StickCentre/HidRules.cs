namespace JesusKpekpes;

internal sealed class StickCentreLinkException : Exception
{
    public StickCentreLinkException(string message) : base(message) { }
}

// The decisions, with no Windows calls in them, so they can be tested without a controller.
internal static class HidRules
{
    internal const ushort SonyVendorId = 0x054C;
    internal const ushort DualSenseProductId = 0x0CE6;
    internal const ushort DualSenseEdgeProductId = 0x0DF2;
    internal const int UsbInputReportLength = 64;

    internal sealed record Candidate(string Path, string Model, bool Usb, int InputLength, int FeatureLength);

    // Windows accepts a feature-report write only at the device's exact feature-report size. Shorter commands are padded with zeros;
    // a command already that long (or longer, or an unknown size) is sent as it is.
    internal static byte[] PadFeature(byte[] data, int featureLength)
    {
        if (featureLength <= data.Length) return data;
        var padded = new byte[featureLength];
        Array.Copy(data, padded, data.Length);
        return padded;
    }

    // Only these two products are ever opened for read or write. Anything else, including look-alikes, is skipped.
    internal static string? ModelFor(ushort vendorId, ushort productId) =>
        vendorId != SonyVendorId ? null
        : productId == DualSenseProductId ? "DualSense"
        : productId == DualSenseEdgeProductId ? "DualSense Edge"
        : null;

    // USB reports are 64 bytes (id 0x01). Bluetooth reports are longer (id 0x31), and its path goes through the Bluetooth
    // enumerator. Unknown length is treated as not USB, so a test or apply is refused rather than guessed.
    internal static bool LooksUsb(string path, int inputLength) =>
        inputLength == UsbInputReportLength
        && !path.Contains("bthenum", StringComparison.OrdinalIgnoreCase)
        && !path.Contains("{00001124-0000-1000-8000-00805f9b34fb}", StringComparison.OrdinalIgnoreCase);

    // A USB pad wins over a Bluetooth one, because the calibration commands are USB only.
    internal static Candidate? Prefer(IReadOnlyList<Candidate> candidates) =>
        candidates.FirstOrDefault(c => c.Usb) ?? candidates.FirstOrDefault();

    internal static string Explain(int win32Error, bool hidHide) => win32Error switch
    {
        5 when hidHide =>
            "Windows will not let this app open the controller, and HidHide is installed. HidHide hides the pad from every app except the ones on its list. " +
            "Add Jesus Kpekpes to the HidHide application list (HidHide Configuration Client), then try again. This app does not change HidHide.",
        5 => "Windows refused access to the controller. Close any program that has it open exclusively and try again.",
        32 => "Another program has the controller open without allowing sharing. Close it (for example a calibration web page in a browser) and try again.",
        2 or 3 => "The controller was unplugged. Plug it in by USB and try again.",
        _ => "The controller could not be opened (Windows error " + win32Error + "). Unplug it, plug it in again by USB and try again."
    };

    // HidHide installs a service of that name. Reading the registry key changes nothing.
    internal static bool HidHideInstalled()
    {
        try
        {
            if (!OperatingSystem.IsWindows()) return false;
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\HidHide");
            return key is not null;
        }
        catch
        {
            return false;
        }
    }
}
