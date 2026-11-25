using System.Diagnostics;

namespace JesusKpekpes;

// Glitch capture: a read-only test for a stick that sometimes snaps away from centre or to an extreme on its own.
// It reads the pad's own input reports (before DS4Windows or any game sees them), so it can say whether a false deflection comes from the
// controller hardware or from something later. It sends no feature report and writes nothing. Run it only when the owner asks, with the pad
// lying still on a table and nobody touching it. A few reports in a row that jump far from rest, while the gyro says the pad did not move,
// are counted as events. Limits are PROVISIONAL until measured on a real DualSense Edge.
internal static class GlitchTest
{
    internal const int DefaultSeconds = 60;
    internal const float StartCounts = 40f;   // distance from rest that starts an event
    internal const float EndCounts = 20f;     // distance from rest that ends it
    internal const float ExtremeCounts = 100f;
    internal const int GyroMovedCounts = 400; // gyro change between neighbouring reports that means the pad was bumped
    private const byte UsbInputReportId = 0x01;
    private const int MinReportLength = 22;

    internal readonly record struct Event(string Stick, double StartMs, double DurationMs, float Peak, bool ReachedExtreme);
    internal readonly record struct Sample(double Ms, byte Lx, byte Ly, byte Rx, byte Ry, short Gx, short Gy, short Gz);

    internal sealed record Result(bool Supported, bool Completed, string? Error, double Seconds, int Reports,
        IReadOnlyList<Event> Events, int IgnoredBecauseMoved, string PlainLanguage);

    internal static Result Run(IControllerLink link, int seconds = DefaultSeconds)
    {
        ArgumentNullException.ThrowIfNull(link);
        if (!link.IsUsb) return Refused("Connect the controller by USB. This test does not run over Bluetooth.");
        if (link.Model is not ("DualSense" or "DualSense Edge")) return Refused("This test supports only a genuine DualSense or DualSense Edge.");

        var samples = new List<Sample>(seconds * 260);
        string? error = null;
        var length = TimeSpan.FromSeconds(Math.Clamp(seconds, 10, 300));
        var started = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(started) < length)
        {
            byte[]? r;
            try { r = link.ReadInputReport(100); }
            catch (Exception ex) { error = "Controller reading stopped: " + ex.Message; break; }
            if (r is null) { Thread.Sleep(1); continue; }
            if (r.Length < MinReportLength || r[0] != UsbInputReportId) continue;
            samples.Add(new Sample(Stopwatch.GetElapsedTime(started).TotalMilliseconds, r[1], r[2], r[3], r[4],
                (short)(r[16] | (r[17] << 8)), (short)(r[18] | (r[19] << 8)), (short)(r[20] | (r[21] << 8))));
        }
        var seconds2 = Stopwatch.GetElapsedTime(started).TotalSeconds;
        var completed = error is null && seconds2 >= length.TotalSeconds - 0.5;
        var (events, moved) = Analyse(samples);
        return new Result(true, completed, error, seconds2, samples.Count, events, moved, Describe(events, moved, samples.Count, seconds2, completed, error));
    }

    /// <summary>Pure analysis, so it can be tested with made-up reports. Rest is the middle value of each axis over the whole capture.</summary>
    internal static (IReadOnlyList<Event> Events, int IgnoredBecauseMoved) Analyse(IReadOnlyList<Sample> samples)
    {
        var events = new List<Event>();
        if (samples.Count < 50) return (events, 0);
        var ignored = 0;
        foreach (var stick in new[] { "left", "right" })
        {
            var left = stick == "left";
            var restX = Median(samples.Select(s => left ? s.Lx : s.Rx));
            var restY = Median(samples.Select(s => left ? s.Ly : s.Ry));
            var inEvent = false; double start = 0, last = 0; float peak = 0; var bumped = false;
            for (var i = 0; i < samples.Count; i++)
            {
                var s = samples[i];
                var dx = (left ? s.Lx : s.Rx) - restX; var dy = (left ? s.Ly : s.Ry) - restY;
                var distance = (float)Math.Sqrt(dx * dx + dy * dy);
                var moved = i > 0 && (Math.Abs(s.Gx - samples[i - 1].Gx) > GyroMovedCounts || Math.Abs(s.Gy - samples[i - 1].Gy) > GyroMovedCounts || Math.Abs(s.Gz - samples[i - 1].Gz) > GyroMovedCounts);
                if (!inEvent && distance > StartCounts) { inEvent = true; start = s.Ms; peak = distance; bumped = moved; }
                else if (inEvent)
                {
                    peak = Math.Max(peak, distance); bumped |= moved;
                    if (distance < EndCounts)
                    {
                        // A bump of the pad itself is not a stick fault.
                        if (bumped) ignored++; else events.Add(new Event(stick, start, last - start, peak, peak >= ExtremeCounts));
                        inEvent = false;
                    }
                }
                last = s.Ms;
            }
            if (inEvent) { if (bumped) ignored++; else events.Add(new Event(stick, start, last - start, peak, peak >= ExtremeCounts)); }
        }
        return (events, ignored);
    }

    private static string Describe(IReadOnlyList<Event> events, int moved, int reports, double seconds, bool completed, string? error)
    {
        if (error is not null) return error;
        if (reports < 50) return "Too few reports arrived to judge. Check the USB cable and try again.";
        var head = $"Watched {seconds:0} seconds, {reports} reports, nobody touching the pad.";
        if (!completed) head += " The capture stopped early.";
        if (events.Count == 0)
            return head + " No stick moved on its own. If the fault still shows in the game, the controller is not sending it in this window: run it longer, and compare with DS4Windows settings and the game.";
        var worst = events.OrderByDescending(e => e.Peak).First();
        var extreme = events.Count(e => e.ReachedExtreme);
        return head + $" The {worst.Stick} stick moved on its own {events.Count} time(s), {extreme} of them to an extreme (up to {worst.Peak:0} counts from rest, longest {events.Max(e => e.DurationMs):0} ms)."
            + " The reports come straight from the controller, before DS4Windows or any game, so this is the controller's stick sensor, not a setting."
            + " Software calibration cannot fix this. A new stick module (the DualSense Edge has replaceable ones) is the real repair."
            + (moved > 0 ? $" ({moved} movement(s) were ignored because the pad itself was bumped.)" : "");
    }

    private static Result Refused(string message) => new(false, false, message, 0, 0, [], 0, message);

    private static float Median(IEnumerable<byte> values)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        return sorted[sorted.Length / 2];
    }
}
