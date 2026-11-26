using System.Diagnostics;

namespace JesusKpekpes;

// Guard for anything that runs by itself (auto-apply on open or reconnect). It listens for a quarter of a second and says whether the
// pad looks untouched: no button, both triggers released, sticks close to rest, gyro still. If not, the automatic action is skipped, so
// nothing is ever sent to a pad someone is playing with. Reading only; it sends no feature report. Limits are PROVISIONAL.
internal static class PadQuiet
{
    internal readonly record struct Result(bool Quiet, string Message);

    internal const int WindowMs = 250;
    internal const int MinReports = 15;
    private const int MinReportLength = 22;

    internal static Result Check(IControllerLink link, int windowMs = WindowMs)
    {
        ArgumentNullException.ThrowIfNull(link);
        var reports = new List<byte[]>(80);
        var started = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(started).TotalMilliseconds < windowMs)
        {
            var r = link.ReadInputReport(50);
            if (r is null) { Thread.Sleep(1); continue; }
            reports.Add(r);
        }
        return Judge(reports);
    }

    internal static Result Judge(IReadOnlyList<byte[]> reports)
    {
        var usable = reports.Where(r => r.Length >= MinReportLength && r[0] == 0x01).ToList();
        if (usable.Count < MinReports) return new Result(false, "Could not tell whether the controller is in use, so nothing was changed.");
        foreach (var r in usable)
        {
            if (r[5] > RestWatch.TriggerRestCounts || r[6] > RestWatch.TriggerRestCounts)
                return new Result(false, "The controller is in use (a trigger is pressed), so nothing was changed.");
            if ((r[8] & 0x0F) != 0x08 || (r[8] & 0xF0) != 0 || r[9] != 0 || (r[10] & 0x03) != 0)
                return new Result(false, "The controller is in use (a button is pressed), so nothing was changed.");
            foreach (var i in new[] { 1, 2, 3, 4 })
                if (Math.Abs(r[i] - StickMath.Rest) > RestWatch.MaxOffsetCounts)
                    return new Result(false, "A stick is held away from the middle, so nothing was changed.");
        }
        foreach (var (lo, hi) in new[] { (16, 17), (18, 19), (20, 21) })
        {
            var vals = usable.Select(r => (short)(r[lo] | (r[hi] << 8))).ToList();
            if (vals.Max() - vals.Min() > RestWatch.GyroSteadyCounts)
                return new Result(false, "The controller is being moved, so nothing was changed.");
        }
        return new Result(true, "The controller is at rest.");
    }
}
