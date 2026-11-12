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
            var liftDb = 10f - Reduction(strongest, -20f, 1f);
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

    /// <summary>
    /// Nova Pro only. The weight band and the tick band are lifted while they are a hit, by one gain for every ear,
    /// so the level gap between the ears stays. A steady tone in those bands is not lifted. The rest of the sound,
    /// including the background, is not given that lift. A gun is still trimmed on the ear that has it.
    /// </summary>
    public static void ShapeNova(float[][] ears, int sampleRate, int hit)
    {
        var count = ears.Length;
        if (count == 0) return;
        var length = ears.Min(e => e.Length);
        var rate = Rate(sampleRate);
        var amount = Math.Clamp(hit, 0, 100) / 100f;
        var maxWeight = 12f * amount;
        var maxTick = 16f * amount;
        var fastA = 1f - MathF.Exp(-1f / (rate * 0.0025f));
        var fastR = 1f - MathF.Exp(-1f / (rate * 0.040f));
        var slowA = 1f - MathF.Exp(-1f / (rate * 0.080f));
        var slowR = 1f - MathF.Exp(-1f / (rate * 0.400f));
        var gunAttack = 1f - MathF.Exp(-1f / (rate * 0.003f));
        var gunRelease = 1f - MathF.Exp(-1f / (rate * 0.045f));
        var weight = new Bandpass[count];
        var tick = new Bandpass[count];
        var gun = new Bandpass[count];
        for (var k = 0; k < count; k++)
        {
            weight[k] = Bandpass.Open(220f, 1.0f, rate);
            tick[k] = Bandpass.Open(3200f, 1.1f, rate);
            gun[k] = Bandpass.Open(1400f, 0.7f, rate);
        }
        var weightFast = new float[count];
        var weightSlow = new float[count];
        var tickFast = new float[count];
        var tickSlow = new float[count];
        var gunEnv = new float[count];
        var weightBand = new float[count];
        var tickBand = new float[count];
        var gunBand = new float[count];
        var other = new float[count];
        const float ceiling = 0.8912509f;
        for (var i = 0; i < length; i++)
        {
            var bestWeightFast = 0f;
            var bestWeightRatio = 1f;
            var bestTickFast = 0f;
            var bestTickRatio = 1f;
            for (var k = 0; k < count; k++)
            {
                var x = ears[k][i];
                weightBand[k] = weight[k].Tick(x);
                tickBand[k] = tick[k].Tick(x);
                gunBand[k] = gun[k].Tick(x);
                other[k] = x - weightBand[k] - tickBand[k] - gunBand[k];
                Follow(ref weightFast[k], MathF.Abs(weightBand[k]), fastA, fastR);
                Follow(ref weightSlow[k], MathF.Abs(weightBand[k]), slowA, slowR);
                Follow(ref tickFast[k], MathF.Abs(tickBand[k]), fastA, fastR);
                Follow(ref tickSlow[k], MathF.Abs(tickBand[k]), slowA, slowR);
                Follow(ref gunEnv[k], MathF.Abs(gunBand[k]), gunAttack, gunRelease);
                Consider(weightFast[k], weightSlow[k], ref bestWeightFast, ref bestWeightRatio);
                Consider(tickFast[k], tickSlow[k], ref bestTickFast, ref bestTickRatio);
            }
            var weightGain = TransientGain(bestWeightFast, bestWeightRatio, maxWeight);
            var tickGain = TransientGain(bestTickFast, bestTickRatio, maxTick);
            var punch = weightGain > tickGain ? weightGain : tickGain;
            var duck = Aside(punch);
            for (var k = 0; k < count; k++)
            {
                var gunGain = MathF.Pow(10f, -Reduction(gunEnv[k], -16f, 2f / 3f) / 20f);
                var y = other[k] * duck + weightBand[k] * weightGain + tickBand[k] * tickGain + gunBand[k] * gunGain;
                var mag = MathF.Abs(y);
                if (mag > ceiling) y = MathF.CopySign(ceiling + MathF.Tanh((mag - ceiling) / 0.05f) * 0.05f, y);
                ears[k][i] = y;
            }
        }
    }

    static void Follow(ref float env, float level, float attack, float release)
    {
        env += (level - env) * (level > env ? attack : release);
        if (env < 1e-20f) env = 0f;
    }

    static void Consider(float fast, float slow, ref float bestFast, ref float bestRatio)
    {
        if (fast > bestFast) bestFast = fast;
        var ratio = fast / (slow + 1e-5f);
        if (ratio > bestRatio) bestRatio = ratio;
    }

    static float TransientGain(float fast, float ratio, float maxDb)
    {
        if (maxDb <= 0.05f || fast < 1e-6f || ratio < 1.15f) return 1f;
        var novelty = 20f * MathF.Log10(ratio);
        if (novelty > 18f) novelty = 18f;
        var edge = novelty / 12f;
        if (edge > 1f) edge = 1f;
        var level = 20f * MathF.Log10(fast + 1e-8f);
        var quiet = (-2f - level) / 22f;
        if (quiet < 0.65f) quiet = 0.65f;
        if (quiet > 1f) quiet = 1f;
        var db = maxDb * quiet * edge;
        if (db <= 0f) return 1f;
        return MathF.Pow(10f, db / 20f);
    }

    static float Aside(float gain)
    {
        if (gain <= 1.02f) return 1f;
        var t = (gain - 1f) / 2.5f;
        if (t > 1f) t = 1f;
        return MathF.Pow(10f, -10f * t / 20f);
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
