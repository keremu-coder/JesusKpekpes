namespace JesusKpekpes;

// Stick test maths for the "Stick centre" tab.
// Pure functions only: no device access, no clocks, no threads, no files.
// Nothing in this file may ever run in the input path while a game is being played.
// Owner of this file: Work package B (see docs/STICK_CENTRE.md). Everything marked PROVISIONAL
// must be checked against a real DualSense or DualSense Edge before it is trusted.
static class StickMath
{
    // DualSense USB input report 0x01: bytes 1 to 4 are left X, left Y, right X, right Y, each 0 to 255.
    // The electrical middle is 127.5. A report cannot represent that, so a healthy stick sits on 127 or 128.
    // One count past that pair (a mean that has reached 129 or 126) is a real rest bias. The old 3% line
    // was about four counts and hid the slow spin this tab exists to remove. PROVISIONAL on a real Edge.
    public const float Rest = 127.5f;
    public const float OneCountPercent = 1f / Rest * 100f;

    // A steady mean at least one USB count from 127.5. A stick that only reports 128 is left alone.
    public const float OffsetWarnPercent = OneCountPercent;
    public const float NoiseWarnCounts = 2.5f;   // resting standard deviation, in counts
    public const float WanderWarnCounts = 8f;    // resting peak-to-peak, in counts

    public static float CountsFromPercent(float offsetPercent) => offsetPercent / OneCountPercent;

    public readonly record struct AxisStats(int Samples, float Mean, float OffsetPercent, float StdDev, int Min, int Max)
    {
        public int PeakToPeak => Samples == 0 ? 0 : Max - Min;
    }

    public readonly record struct StickVerdict(bool EnoughData, bool CentreOff, bool Noisy, string Summary);

    public readonly record struct RateStats(int Reports, double PerSecond, double MedianGapMs, double WorstGapMs, double P99GapMs);

    // One axis, hands off the stick. values are the raw bytes of that axis, in the order they were read.
    public static AxisStats Analyse(IReadOnlyList<byte> values)
    {
        if (values.Count == 0) return new AxisStats(0, 0f, 0f, 0f, 0, 0);
        double sum = 0;
        var min = 255;
        var max = 0;
        for (var i = 0; i < values.Count; i++)
        {
            int v = values[i];
            sum += v;
            if (v < min) min = v;
            if (v > max) max = v;
        }
        var mean = sum / values.Count;
        double squares = 0;
        for (var i = 0; i < values.Count; i++)
        {
            var d = values[i] - mean;
            squares += d * d;
        }
        var deviation = Math.Sqrt(squares / values.Count);
        return new AxisStats(values.Count, (float)mean, (float)((mean - Rest) / Rest * 100.0), (float)deviation, min, max);
    }

    // One stick, both axes. The wording says what a calibration can and cannot fix.
    public static StickVerdict Judge(AxisStats x, AxisStats y)
    {
        if (x.Samples < 25 || y.Samples < 25)
            return new StickVerdict(false, false, false, "Not enough readings yet. Keep your hands off the stick and try again.");
        var radial = MathF.Sqrt(x.OffsetPercent * x.OffsetPercent + y.OffsetPercent * y.OffsetPercent);
        var centreOff = radial >= OffsetWarnPercent;
        var noisy = x.StdDev >= NoiseWarnCounts || y.StdDev >= NoiseWarnCounts
            || x.PeakToPeak >= WanderWarnCounts || y.PeakToPeak >= WanderWarnCounts;
        string summary;
        if (!centreOff && !noisy)
            summary = "The stick rests on the centre codes.";
        else if (centreOff && !noisy)
            summary = "The stick rests at least one count off centre and holds steady. The pad's own centre trim should fix this.";
        else if (!centreOff && noisy)
            summary = "The reading wanders at rest. A calibration will not fix that. Dirt or a worn stick module is more likely.";
        else
            summary = "Off centre and wandering. Calibrate first. If it still wanders, the stick module is worn.";
        return new StickVerdict(true, centreOff, noisy, summary);
    }

    // arrivalMs: when each input report arrived, in milliseconds, all from one steady clock, oldest first.
    // This measures the report rate the controller really delivers, which is what matters for input delay.
    public static RateStats AnalyseRate(IReadOnlyList<double> arrivalMs)
    {
        if (arrivalMs.Count < 3) return new RateStats(arrivalMs.Count, 0, 0, 0, 0);
        var gaps = new double[arrivalMs.Count - 1];
        for (var i = 1; i < arrivalMs.Count; i++) gaps[i - 1] = arrivalMs[i] - arrivalMs[i - 1];
        var span = arrivalMs[arrivalMs.Count - 1] - arrivalMs[0];
        Array.Sort(gaps);
        var p99At = (int)Math.Min(gaps.Length - 1, Math.Floor(gaps.Length * 0.99));
        var perSecond = span > 0 ? (arrivalMs.Count - 1) * 1000.0 / span : 0;
        return new RateStats(arrivalMs.Count, perSecond, gaps[gaps.Length / 2], gaps[gaps.Length - 1], gaps[p99At]);
    }

    // Did a change cost any report rate? True means undo it. PROVISIONAL limits, and conservative:
    // fewer than 25 reports in either short comparison window is too little data, so undo the change.
    public static bool GotWorse(RateStats before, RateStats after)
    {
        if (before.Reports < 25 || after.Reports < 25) return true;
        return after.PerSecond < before.PerSecond * 0.9 || after.P99GapMs > before.P99GapMs * 1.1;
    }
}
