namespace JesusKpekpes;

// The text equalizer runs first, at the sample rate it is given.
// This stage raises only the foot band, by the same amount in every ear. A gun is trimmed in its own band, on that ear alone.
public static class TuneEngine
{
    public static void Run(float[] left, float[] right, int sampleRate) =>
        Mix.Process(new MixPlan(Preset.Drop, Route.Stereo, 100, 100, ""), left, right, sampleRate: Rate(sampleRate));

    public static void Run(float[][] ears, int sampleRate)
    {
        if (ears.Length >= 2)
        {
            Run(ears[0], ears[1], sampleRate);
            return;
        }
        if (ears.Length == 1) Compress(ears[0], sampleRate);
    }

    public static void Compress(float[] ear, int sampleRate) => CompressLinked([ear], sampleRate);

    /// <summary>
    /// The foot band gets one shared lift for all ears, taken from the strongest ear, so the loudness gap between the ears is kept.
    /// The lift only ever adds level (never below 1) and eases to 1 as the foot band gets loud. The gun trim stays on each ear alone.
    /// Mirrors plugin/tune.c.
    /// </summary>
    public static void CompressLinked(float[][] ears, int sampleRate)
    {
        var count = ears.Length;
        if (count == 0) return;
        var length = ears.Min(e => e.Length);
        var rate = Rate(sampleRate);
        var footAttack = 1f - MathF.Exp(-1f / (rate * 0.008f));
        var footRelease = 1f - MathF.Exp(-1f / (rate * 0.100f));
        var gunAttack = 1f - MathF.Exp(-1f / (rate * 0.003f));
        var gunRelease = 1f - MathF.Exp(-1f / (rate * 0.045f));
        var gun = new Bandpass[count];
        var foot = new Bandpass[count];
        for (var k = 0; k < count; k++)
        {
            gun[k] = Bandpass.Open(1400f, 0.6f, rate);
            foot[k] = Bandpass.Open(180f, 1.0f, rate);
        }
        var footEnv = new float[count];
        var gunEnv = new float[count];
        var gunBand = new float[count];
        var footBand = new float[count];
        var other = new float[count];
        const float ceiling = 0.8912509f;
        for (var i = 0; i < length; i++)
        {
            var strongest = 0f;
            for (var k = 0; k < count; k++)
            {
                var x = ears[k][i];
                gunBand[k] = gun[k].Tick(x);
                footBand[k] = foot[k].Tick(x);
                other[k] = x - gunBand[k] - footBand[k];
                var footLevel = MathF.Abs(footBand[k]);
                var gunLevel = MathF.Abs(gunBand[k]);
                footEnv[k] += (footLevel - footEnv[k]) * (footLevel > footEnv[k] ? footAttack : footRelease);
                gunEnv[k] += (gunLevel - gunEnv[k]) * (gunLevel > gunEnv[k] ? gunAttack : gunRelease);
                if (footEnv[k] < 1e-20f) footEnv[k] = 0f;
                if (gunEnv[k] < 1e-20f) gunEnv[k] = 0f;
                if (footEnv[k] > strongest) strongest = footEnv[k];
            }
            var liftDb = 6f - Reduction(strongest, -20f, 0.5f);
            var footGain = liftDb > 0f ? MathF.Pow(10f, liftDb / 20f) : 1f;
            for (var k = 0; k < count; k++)
            {
                var gunGain = MathF.Pow(10f, -Reduction(gunEnv[k], -16f, 2f / 3f) / 20f);
                var y = other[k] + footBand[k] * footGain + gunBand[k] * gunGain;
                var mag = MathF.Abs(y);
                if (mag > ceiling) y = MathF.CopySign(ceiling + MathF.Tanh((mag - ceiling) / 0.05f) * 0.05f, y);
                ears[k][i] = y;
            }
        }
    }

    static int Rate(int sampleRate) => sampleRate >= 1000 ? sampleRate : 48000;

    static float Reduction(float env, float threshold, float slope)
    {
        var over = 20f * MathF.Log10(env + 1e-8f) - threshold;
        if (over > 3f) return (over - 3f) * slope;
        if (over > -3f)
        {
            var t = over + 3f;
            return slope * t * t / 12f;
        }
        return 0f;
    }

    struct Bandpass
    {
        float _b0, _b2, _a1, _a2, _x1, _x2, _y1, _y2;

        public static Bandpass Open(float hz, float q, int sampleRate)
        {
            var w0 = 2f * MathF.PI * hz / sampleRate;
            var cos = MathF.Cos(w0);
            var alpha = MathF.Sin(w0) / (2f * q);
            var a0 = 1f + alpha;
            return new Bandpass { _b0 = alpha / a0, _b2 = -alpha / a0, _a1 = -2f * cos / a0, _a2 = (1f - alpha) / a0 };
        }

        public float Tick(float x)
        {
            var y = _b0 * x + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
            if (MathF.Abs(y) < 1e-20f) y = 0f;
            _x2 = _x1;
            _x1 = x;
            _y2 = _y1;
            _y1 = y;
            return y;
        }
    }
}
