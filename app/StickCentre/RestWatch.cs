namespace JesusKpekpes;

// Rest watch: decides, from input reports it is handed, whether a stick is stuck a little off centre while nobody is using the pad.
// It is a pure state machine. It opens nothing, starts no thread or timer and sends nothing; the caller feeds it reports it already
// has. That keeps this file out of the input path: it adds no delay to anything. Whatever the caller does with a verdict
// (one clamped centre correction, within the limits in docs/STICK_CENTRE.md) is the caller's job and is checked there.
//
// Why "at rest" is judged from the whole pad and not the stick alone: a hand holding a stick gently and a stick stuck by dirt both
// look steady. The difference is that a held pad moves. So a stick is called stuck only when, for the whole window, no button is
// down, both triggers are released, the gyro shows the pad lying still, the stick does not move, and the offset is small (a
// deliberate push is never "corrected"). All the numbers below are PROVISIONAL until measured on a real DualSense Edge.
internal sealed class RestWatch
{
    internal enum Side { None, Left, Right }
    internal enum State { Active, Settling, Resting, StuckOffCentre }

    internal readonly record struct Verdict(State State, Side Stick, float OffsetX, float OffsetY, string Reason);

    // Provisional limits.
    internal const int WindowMs = 3000;          // how long everything must hold still
    internal const int CooldownMs = 30000;       // after a verdict, stay quiet this long
    internal const float MinOffsetCounts = 2.5f; // smaller than this is noise, not a fault
    internal const float MaxOffsetCounts = 14f;  // bigger than this could be a deliberate push, so it is never corrected
    internal const int StickSteadyCounts = 3;    // peak to peak over the window
    internal const int GyroSteadyCounts = 80;    // peak to peak per axis over the window
    internal const int TriggerRestCounts = 4;
    private const byte UsbInputReportId = 0x01;
    private const int MinReportLength = 28;      // up to the last accelerometer byte of a USB report

    private readonly Queue<Sample> _window = new();
    private long _quietUntilMs = long.MinValue;

    private readonly record struct Sample(long Ms, byte Lx, byte Ly, byte Rx, byte Ry, byte Buttons1, byte Buttons2, byte Buttons3, short Gx, short Gy, short Gz);

    /// <summary>Hand it each input report with the time it arrived. Returns the current verdict. Reports that are not USB input reports are ignored.</summary>
    internal Verdict Feed(byte[] report, long nowMs)
    {
        if (report is null || report.Length < MinReportLength || report[0] != UsbInputReportId)
            return new Verdict(State.Active, Side.None, 0, 0, "Not a USB input report.");

        var s = new Sample(nowMs, report[1], report[2], report[3], report[4], report[8], report[9], (byte)(report[10] & 0x03),
            (short)(report[16] | (report[17] << 8)), (short)(report[18] | (report[19] << 8)), (short)(report[20] | (report[21] << 8)));

        // Anything that says "someone is using the pad" empties the window, so a verdict needs a fresh quiet stretch.
        if (report[5] > TriggerRestCounts || report[6] > TriggerRestCounts || IsButtonDown(s))
        {
            _window.Clear();
            return new Verdict(State.Active, Side.None, 0, 0, "The pad is in use.");
        }

        _window.Enqueue(s);
        while (_window.Count > 0 && nowMs - _window.Peek().Ms > WindowMs) _window.Dequeue();

        var span = nowMs - _window.Peek().Ms;
        if (span < WindowMs - 250 || _window.Count < 10)
            return new Verdict(State.Settling, Side.None, 0, 0, "Watching for a quiet stretch.");

        var samples = _window.ToArray();
        if (Range(samples, x => x.Gx) > GyroSteadyCounts || Range(samples, x => x.Gy) > GyroSteadyCounts || Range(samples, x => x.Gz) > GyroSteadyCounts)
            return new Verdict(State.Settling, Side.None, 0, 0, "The pad is being moved.");

        var lxRange = Range(samples, x => x.Lx); var lyRange = Range(samples, x => x.Ly);
        var rxRange = Range(samples, x => x.Rx); var ryRange = Range(samples, x => x.Ry);
        var left = Judge(samples, Side.Left, lxRange, lyRange, x => x.Lx, x => x.Ly);
        var right = Judge(samples, Side.Right, rxRange, ryRange, x => x.Rx, x => x.Ry);

        if (nowMs < _quietUntilMs)
            return new Verdict(State.Resting, Side.None, 0, 0, "Recently handled; waiting before looking again.");

        // If both sticks look stuck, the pad is probably not lying still or the fault is not a stick; do nothing.
        if (left is not null && right is not null)
            return new Verdict(State.Resting, Side.None, 0, 0, "Both sticks are off centre together; not treated as a stick fault.");

        var found = left ?? right;
        if (found is null) return new Verdict(State.Resting, Side.None, 0, 0, "Both sticks are at rest.");

        _quietUntilMs = nowMs + CooldownMs;
        _window.Clear();
        return found.Value;
    }

    internal void Reset()
    {
        _window.Clear();
        _quietUntilMs = long.MinValue;
    }

    private static bool IsButtonDown(Sample s)
    {
        // Byte 8: the d-pad lives in the low nibble (8 = neutral) and the face buttons in the high nibble.
        var dpadNeutral = (s.Buttons1 & 0x0F) == 0x08;
        return !dpadNeutral || (s.Buttons1 & 0xF0) != 0 || s.Buttons2 != 0 || s.Buttons3 != 0;
    }

    private static Verdict? Judge(Sample[] samples, Side side, int rangeX, int rangeY, Func<Sample, byte> x, Func<Sample, byte> y)
    {
        if (rangeX > StickSteadyCounts || rangeY > StickSteadyCounts) return null;
        var offsetX = (float)samples.Average(s => (double)x(s)) - StickMath.Rest;
        var offsetY = (float)samples.Average(s => (double)y(s)) - StickMath.Rest;
        var biggest = Math.Max(Math.Abs(offsetX), Math.Abs(offsetY));
        if (biggest < MinOffsetCounts || biggest > MaxOffsetCounts) return null;
        return new Verdict(State.StickOffCentre, side, offsetX, offsetY,
            $"The {(side == Side.Left ? "left" : "right")} stick is resting {offsetX:+0.0;-0.0;0} across and {offsetY:+0.0;-0.0;0} up/down off centre while the pad lies still.");
    }

    private static int Range(Sample[] samples, Func<Sample, int> pick)
    {
        var min = int.MaxValue; var max = int.MinValue;
        foreach (var s in samples) { var v = pick(s); if (v < min) min = v; if (v > max) max = v; }
        return max - min;
    }
}
