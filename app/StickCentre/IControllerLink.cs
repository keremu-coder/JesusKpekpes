namespace JesusKpekpes;

// The one door to the controller. Everything above this (the test, the live apply, the tab) talks to this
// interface only, so the maths and the safety rules can be tested with a fake controller and no hardware.
// Owner of this file: Work package A (see docs/STICK_CENTRE.md). The real implementation is a short-lived
// Windows HID handle. It is opened for the test or the apply, for well under a second, and then closed.
// Nothing here may keep a handle open or send feature reports while a game is being played.
interface IControllerLink : IDisposable
{
    // "DualSense" or "DualSense Edge". Anything else is refused by the code that opens the link.
    string Model { get; }

    // True only for a USB connection. The calibration commands are not supported over Bluetooth here.
    bool IsUsb { get; }

    // Next input report, or null if none arrived within timeoutMs. Includes the report id as byte 0.
    byte[]? ReadInputReport(int timeoutMs);

    // Feature report get. Returns the bytes including the report id, or null on failure.
    byte[]? GetFeatureReport(byte reportId, int length);

    // Feature report set. data[0] is the report id. True if Windows accepted it.
    bool SendFeatureReport(byte[] data);

    // Plain-language detail of the last failed SendFeatureReport, for the error message. Empty when unknown.
    string LastFailure => "";
}
