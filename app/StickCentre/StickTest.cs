using System.Diagnostics;

namespace JesusKpekpes;

// A bounded, read-only diagnostic. This is called only when the user explicitly starts a test;
// it is never a polling service and it never sends feature reports or writes controller settings.
internal static class StickTest
{
    public static readonly TimeSpan SampleDuration = TimeSpan.FromSeconds(5);
    private const int ReadTimeoutCapMs = 100;
    private const byte UsbInputReportId = 0x01;

    internal readonly record struct StickResult(StickMath.AxisStats X, StickMath.AxisStats Y, StickMath.StickVerdict Verdict);

    internal sealed record Result(
        bool Supported,
        bool Completed,
        string? Error,
        TimeSpan Duration,
        StickResult? LeftStick,
        StickResult? RightStick,
        StickMath.RateStats ReportRate,
        int ValidReports,
        int IgnoredReports,
        string PlainLanguage);

    /// <summary>
    /// Samples five seconds of USB input reports. The caller owns the link and must keep both
    /// sticks untouched during the test. This method never disposes the link or sends reports.
    /// </summary>
    internal static Result Run(IControllerLink link)
    {
        ArgumentNullException.ThrowIfNull(link);

        if (!link.IsUsb)
            return Refused("Connect the controller by USB. Stick-centre testing is not supported over Bluetooth.");
        if (link.Model is not ("DualSense" or "DualSense Edge"))
            return Refused("This test supports only a genuine DualSense or DualSense Edge controller.");

        var leftX = new List<byte>(1024);
        var leftY = new List<byte>(1024);
        var rightX = new List<byte>(1024);
        var rightY = new List<byte>(1024);
        var arrivals = new List<double>(1024);
        var ignored = 0;
        string? error = null;
        var started = Stopwatch.GetTimestamp();

        while (Stopwatch.GetElapsedTime(started) < SampleDuration)
        {
            var elapsed = Stopwatch.GetElapsedTime(started);
            var remaining = SampleDuration - elapsed;
            var timeoutMs = Math.Clamp((int)Math.Ceiling(remaining.TotalMilliseconds), 1, ReadTimeoutCapMs);
            byte[]? report;
            try
            {
                report = link.ReadInputReport(timeoutMs);
            }
            catch (Exception ex)
            {
                error = $"Controller reading stopped: {ex.Message}";
                break;
            }

            if (Stopwatch.GetElapsedTime(started) >= SampleDuration) break;
            if (report is null)
            {
                // The link normally waits up to timeoutMs. Yield briefly as a safeguard for
                // implementations that return null immediately, avoiding a tight polling loop.
                Thread.Sleep(1);
                continue;
            }
            if (report.Length < 5 || report[0] != UsbInputReportId)
            {
                ignored++;
                continue;
            }

            leftX.Add(report[1]);
            leftY.Add(report[2]);
            rightX.Add(report[3]);
            rightY.Add(report[4]);
            arrivals.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        var duration = Stopwatch.GetElapsedTime(started);
        var rate = StickMath.AnalyseRate(arrivals);
        var left = MakeStick(leftX, leftY);
        var right = MakeStick(rightX, rightY);
        var completed = error is null && duration >= SampleDuration;
        var text = Describe(left, right, rate, completed, error, ignored);
        return new Result(true, completed, error, duration, left, right, rate, arrivals.Count, ignored, text);
    }

    private static Result Refused(string message) => new(
        Supported: false,
        Completed: false,
        Error: message,
        Duration: TimeSpan.Zero,
        LeftStick: null,
        RightStick: null,
        ReportRate: new StickMath.RateStats(0, 0, 0, 0, 0),
        ValidReports: 0,
        IgnoredReports: 0,
        PlainLanguage: message);

    private static StickResult MakeStick(List<byte> x, List<byte> y)
    {
        var xStats = StickMath.Analyse(x);
        var yStats = StickMath.Analyse(y);
        return new StickResult(xStats, yStats, StickMath.Judge(xStats, yStats));
    }

    private static string Describe(
        StickResult left,
        StickResult right,
        StickMath.RateStats rate,
        bool completed,
        string? error,
        int ignored)
    {
        var lines = new List<string>
        {
            completed
                ? "Five-second hands-off stick test complete. No controller settings were changed."
                : "Stick test incomplete. No controller settings were changed.",
            "Left stick: " + left.Verdict.Summary,
            $"  X offset {left.X.OffsetPercent:+0.0;-0.0;0.0}% (noise {left.X.StdDev:0.0}, range {left.X.PeakToPeak}); Y offset {left.Y.OffsetPercent:+0.0;-0.0;0.0}% (noise {left.Y.StdDev:0.0}, range {left.Y.PeakToPeak}).",
            "Right stick: " + right.Verdict.Summary,
            $"  X offset {right.X.OffsetPercent:+0.0;-0.0;0.0}% (noise {right.X.StdDev:0.0}, range {right.X.PeakToPeak}); Y offset {right.Y.OffsetPercent:+0.0;-0.0;0.0}% (noise {right.Y.StdDev:0.0}, range {right.Y.PeakToPeak}).",
            rate.Reports >= 3
                ? $"Observed report rate: {rate.PerSecond:0.0} reports/second; worst gap {rate.WorstGapMs:0.0} ms (p99 {rate.P99GapMs:0.0} ms). This is measured at the app, not a guaranteed end-to-end game latency."
                : "Report rate unavailable: too few valid controller reports were received."
        };

        if (ignored > 0) lines.Add($"Ignored {ignored} malformed or unexpected input report(s).");
        if (error is not null) lines.Add(error);
        if (!completed && error is null) lines.Add("The sampling window ended early.");
        return string.Join(Environment.NewLine, lines);
    }
}
