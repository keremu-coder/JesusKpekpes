namespace JesusKpekpes;

// The text equalizer runs first, at the sample rate it is given.
// This stage raises only the foot band. A gun is trimmed in its own band, on that ear alone.
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

    public static void Compress(float[] ear, int sampleRate)
    {
        var rate = Rate(sampleRate);
        var footAttack = 1f - MathF.Exp(-1f / (rate * 0.008f));
        var footRelease = 1f - MathF.Exp(-1f / (rate * 0.100f));
        var gunAttack = 1f - MathF.Exp(-1f / (rate * 0.003f));
        var gunRelease = 1f - MathF.Exp(-1f / (rate * 0.045f));
        var gun = Bandpass.Open(1400f, 0.6f, rate);
        var foot = Bandpass.Open(180f, 1.0f, rate);
        var footEnv = 0f;
        var gunEnv = 0f;
        const float ceiling = 0.8912509f;
        for (var i = 0; i < ear.Length; i++)
        {
            var gunBand = gun.Tick(ear[i]);
            var footBand = foot.Tick(ear[i]);
            var other = ear[i] - gunBand - footBand;
            var footLevel = MathF.Abs(footBand);
            var gunLevel = MathF.Abs(gunBand);
            footEnv += (footLevel - footEnv) * (footLevel > footEnv ? footAttack : footRelease);
            gunEnv += (gunLevel - gunEnv) * (gunLevel > gunEnv ? gunAttack : gunRelease);
            var footGain = MathF.Pow(10f, (-Reduction(footEnv, -20f, 0.5f) + 6f) / 20f);
            var gunGain = MathF.Pow(10f, -Reduction(gunEnv, -16f, 2f / 3f) / 20f);
            var y = other + footBand * footGain + gunBand * gunGain;
            var mag = MathF.Abs(y);
            if (mag > ceiling) y = MathF.CopySign(ceiling + MathF.Tanh((mag - ceiling) / 0.05f) * 0.05f, y);
            ear[i] = y;
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
            _x2 = _x1;
            _x1 = x;
            _y2 = _y1;
            _y1 = y;
            return y;
        }
    }
}
