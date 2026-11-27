using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace JesusKpekpes;

// The settle test measures the fault itself: where does each stick come to rest after it is pushed to the edge and let go?
// A healthy stick comes back to the same point every time. One that lands somewhere different each time cannot be fixed by any
// fixed centre trim; that is a worn module, a weak spring or dirt. Reading only (input reports, plus one read of the stored
// centre values at the start). It changes nothing and is not part of any game's input path. Limits are PROVISIONAL.
internal static class SettleTest
{
    internal const float DeflectCounts = 50f;     // pushed at least this far from the middle counts as "pushed to the edge"
    internal const float RestCounts = 20f;        // back inside this counts as "let go"
    internal const int SettleMs = 400;            // must stay inside for this long to count as resting
    internal const int MeanWindowMs = 250;        // the resting point is the mean of the last part of that time
    internal const int TargetReleases = 10;
    internal const int MaxSeconds = 120;
    internal const float SameSpotCounts = 1.5f;   // spread up to this is "the same point" (a count of jitter is normal)
    internal const float MinorSpreadCounts = 3.5f;

    internal readonly record struct Rest(float X, float Y);

    internal sealed class StickTracker(int xIndex, int yIndex)
    {
        private bool _deflected;
        private double _settleStart = -1;
        private readonly List<(double T, int X, int Y)> _window = [];
        internal List<Rest> Releases { get; } = [];

        internal bool Feed(byte[] report, double nowMs)
        {
            if (report.Length <= Math.Max(xIndex, yIndex)) return false;
            int x = report[xIndex], y = report[yIndex];
            var deviation = Math.Max(Math.Abs(x - StickMath.Rest), Math.Abs(y - StickMath.Rest));
            if (deviation >= DeflectCounts)
            {
                _deflected = true;
                _settleStart = -1;
                _window.Clear();
                return false;
            }
            if (!_deflected) return false;
            if (deviation >= RestCounts)
            {
                _settleStart = -1;
                _window.Clear();
                return false;
            }
            if (_settleStart < 0) _settleStart = nowMs;
            _window.Add((nowMs, x, y));
            if (nowMs - _settleStart < SettleMs) return false;

            var tail = _window.Where(s => s.T >= nowMs - MeanWindowMs).ToList();
            if (tail.Count == 0) tail = _window;
            Releases.Add(new Rest((float)tail.Average(s => s.X) - StickMath.Rest, (float)tail.Average(s => s.Y) - StickMath.Rest));
            _deflected = false;
            _settleStart = -1;
            _window.Clear();
            return true;
        }
    }

    internal static string Describe(string name, IReadOnlyList<Rest> releases)
    {
        if (releases.Count < 3) return $"{name}: only {releases.Count} release(s) were seen; at least 3 are needed for a verdict.";
        var spreadX = releases.Max(r => r.X) - releases.Min(r => r.X);
        var spreadY = releases.Max(r => r.Y) - releases.Min(r => r.Y);
        var spread = Math.Max(spreadX, spreadY);
        var verdict = spread <= SameSpotCounts ? "It comes back to the same point every time."
            : spread <= MinorSpreadCounts ? "It comes back to nearly the same point (a small spread)."
            : "It does NOT come back to the same point. A fixed centre trim cannot fix that; this is the stick itself (wear, spring or dirt).";
        var list = string.Join("  ", releases.Select(r => $"({F(r.X)},{F(r.Y)})"));
        return $"{name}: {verdict}" + Environment.NewLine +
               $"  {releases.Count} releases, resting point in counts (X,Y): {list}" + Environment.NewLine +
               $"  Spread: X {F(spreadX, false)}, Y {F(spreadY, false)} counts.";
    }

    private static string F(float v, bool signed = true) => v.ToString(signed ? "+0.0;-0.0;0.0" : "0.0", CultureInfo.InvariantCulture);

    internal static string StoredValuesLine(IControllerLink link)
    {
        try
        {
            var snapshot = CentreProtocol.ReadCurrent(link);
            return "Centre values stored in the pad right now: " + string.Join(", ", snapshot.Values) + ".";
        }
        catch (Exception ex)
        {
            return "The stored centre values could not be read (" + ex.Message + ").";
        }
    }

    internal static string Run(IControllerLink link, Action<string>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(link);
        if (!link.IsUsb) return "Connect the controller by USB to run the settle test.";
        var stored = StoredValuesLine(link);
        var left = new StickTracker(1, 2);
        var right = new StickTracker(3, 4);
        var clock = Stopwatch.StartNew();
        progress?.Invoke($"Push a stick fully to the edge, let go, and wait for it to settle. Do this {TargetReleases} times on each stick (about a second each). Left 0/{TargetReleases}, right 0/{TargetReleases}.");
        while (clock.Elapsed.TotalSeconds < MaxSeconds && (left.Releases.Count < TargetReleases || right.Releases.Count < TargetReleases))
        {
            var report = link.ReadInputReport(50);
            if (report is null || report.Length < 5 || report[0] != 0x01) continue;
            var now = clock.Elapsed.TotalMilliseconds;
            var a = left.Feed(report, now);
            var b = right.Feed(report, now);
            if (a || b)
                progress?.Invoke($"Counted. Left {left.Releases.Count}/{TargetReleases}, right {right.Releases.Count}/{TargetReleases}. Keep going.");
        }
        var text = new StringBuilder();
        text.AppendLine("Settle test finished. No controller settings were changed.");
        text.AppendLine(Describe("Left stick", left.Releases));
        text.AppendLine(Describe("Right stick", right.Releases));
        text.Append(stored);
        return text.ToString();
    }
}
