namespace JesusKpekpes;

/// <summary>Rough numbers for comparing two recordings of the same clip. Not LUFS, not a lab test.</summary>
sealed record SoundStats(double ActiveDb, double PeakDb, double StepMinusLowDb, double SpreadDb, double ActiveSeconds)
{
    public string Describe() =>
        $"Loudness while sound plays: {ActiveDb:0.0} dB\r\n" +
        $"Loudest sample: {PeakDb:0.0} dB (0 is the limit)\r\n" +
        $"Step range above bass: {StepMinusLowDb:0.0} dB\r\n" +
        $"Loud-to-quiet spread: {SpreadDb:0.0} dB\r\n" +
        $"Sound was present for {ActiveSeconds:0.0} s";

    public static string Compare(SoundStats first, SoundStats second) =>
        $"Second minus first:\r\n" +
        $"  Loudness {second.ActiveDb - first.ActiveDb:+0.0;-0.0;0.0} dB\r\n" +
        $"  Peak {second.PeakDb - first.PeakDb:+0.0;-0.0;0.0} dB\r\n" +
        $"  Step range above bass {second.StepMinusLowDb - first.StepMinusLowDb:+0.0;-0.0;0.0} dB\r\n" +
        $"  Loud-to-quiet spread {second.SpreadDb - first.SpreadDb:+0.0;-0.0;0.0} dB";
}

static class LoudnessMath
{
    const double Silence = -60;

    public static SoundStats Analyze(float[] mono, int rate)
    {
        var block = Math.Max(1, rate / 10);
        var step = new Bq(Bq.Kind.Band, 3400, 0.9, rate);
        var low = new Bq(Bq.Kind.Low, 200, 0.707, rate);
        double peak = 0, activeMs = 0, activeStep = 0, activeLow = 0;
        var blocks = new List<double>();
        var count = 0;
        for (var start = 0; start + block <= mono.Length; start += block)
        {
            double ms = 0, st = 0, lo = 0;
            for (var i = start; i < start + block; i++)
            {
                var x = mono[i];
                ms += x * x;
                var s = step.Tick(x);
                var l = low.Tick(x);
                st += s * s;
                lo += l * l;
                var a = Math.Abs(x);
                if (a > peak) peak = a;
            }
            ms /= block; st /= block; lo /= block;
            var db = 10 * Math.Log10(ms + 1e-12);
            if (db <= Silence) continue;
            blocks.Add(db);
            activeMs += ms; activeStep += st; activeLow += lo;
            count++;
        }
        var peakDb = 20 * Math.Log10(peak + 1e-12);
        if (count == 0) return new SoundStats(Silence, peakDb, 0, 0, 0);
        blocks.Sort();
        double Pick(double p) => blocks[Math.Clamp((int)Math.Round(p * (blocks.Count - 1)), 0, blocks.Count - 1)];
        return new SoundStats(
            10 * Math.Log10(activeMs / count),
            peakDb,
            10 * Math.Log10((activeStep + 1e-12) / (activeLow + 1e-12)),
            Pick(0.95) - Pick(0.10),
            count * (double)block / rate);
    }

    sealed class Bq
    {
        public enum Kind { Band, Low }
        readonly double _b0, _b1, _b2, _a1, _a2;
        double _x1, _x2, _y1, _y2;

        public Bq(Kind kind, double hz, double q, double rate)
        {
            var w = 2 * Math.PI * hz / rate;
            var cos = Math.Cos(w);
            var alpha = Math.Sin(w) / (2 * q);
            var a0 = 1 + alpha;
            if (kind == Kind.Band)
            {
                _b0 = alpha / a0; _b1 = 0; _b2 = -alpha / a0;
            }
            else
            {
                _b0 = (1 - cos) / 2 / a0; _b1 = (1 - cos) / a0; _b2 = (1 - cos) / 2 / a0;
            }
            _a1 = -2 * cos / a0;
            _a2 = (1 - alpha) / a0;
        }

        public double Tick(double x)
        {
            var y = _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
            _x2 = _x1; _x1 = x;
            _y2 = _y1; _y1 = y;
            return y;
        }
    }
}
