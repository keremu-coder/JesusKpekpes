namespace JesusKpekpes;

// The text equalizer runs first, at the sample rate it is given.
// This stage raises the foot band, and the step tick while it is a hit, by the same amount in every ear.
// A gun is trimmed in its own band, on that ear alone.
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
    /// The step tick (shoe grit, 3.4 kHz) gets up to +9 dB, one gain for all ears, only while it is a hit, so a steady tone is not lifted.
    /// While a gun is being trimmed in any ear, the tick lift fades out, so a shot's crack is not sharpened.
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
        var fastA = 1f - MathF.Exp(-1f / (rate * 0.0025f));
        var fastR = 1f - MathF.Exp(-1f / (rate * 0.040f));
        var slowA = 1f - MathF.Exp(-1f / (rate * 0.080f));
        var slowR = 1f - MathF.Exp(-1f / (rate * 0.400f));
        var gun = new Bandpass[count];
        var foot = new Bandpass[count];
        var tick = new Bandpass[count];
        for (var k = 0; k < count; k++)
        {
            gun[k] = Bandpass.Open(1400f, 0.6f, rate);
            foot[k] = Bandpass.Open(180f, 1.0f, rate);
            tick[k] = Bandpass.Open(3400f, 1.4f, rate);
        }
        var footEnv = new float[count];
        var gunEnv = new float[count];
        var tickFast = new float[count];
        var tickSlow = new float[count];
        var gunBand = new float[count];
        var footBand = new float[count];
        var tickBand = new float[count];
        var other = new float[count];
        const float ceiling = 0.8912509f;
        for (var i = 0; i < length; i++)
        {
            var strongest = 0f;
            var bestTickFast = 0f;
            var bestTickRatio = 1f;
            var gunCut = 0f;
            for (var k = 0; k < count; k++)
            {
                var x = ears[k][i];
                gunBand[k] = gun[k].Tick(x);
                footBand[k] = foot[k].Tick(x);
                tickBand[k] = tick[k].Tick(x);
                other[k] = x - gunBand[k] - footBand[k] - tickBand[k];
                var footLevel = MathF.Abs(footBand[k]);
                var gunLevel = MathF.Abs(gunBand[k]);
                footEnv[k] += (footLevel - footEnv[k]) * (footLevel > footEnv[k] ? footAttack : footRelease);
                gunEnv[k] += (gunLevel - gunEnv[k]) * (gunLevel > gunEnv[k] ? gunAttack : gunRelease);
                if (footEnv[k] < 1e-20f) footEnv[k] = 0f;
                if (gunEnv[k] < 1e-20f) gunEnv[k] = 0f;
                Follow(ref tickFast[k], MathF.Abs(tickBand[k]), fastA, fastR);
                Follow(ref tickSlow[k], MathF.Abs(tickBand[k]), slowA, slowR);
                if (footEnv[k] > strongest) strongest = footEnv[k];
                Consider(tickFast[k], tickSlow[k], ref bestTickFast, ref bestTickRatio);
                var cut = Reduction(gunEnv[k], -16f, 2f / 3f);
                if (cut > gunCut) gunCut = cut;
            }
            var liftDb = 10f - Reduction(strongest, -20f, 1f);
            var footGain = liftDb > 0f ? MathF.Pow(10f, liftDb / 20f) : 1f;
            var tickGain = ClassicTickGain(bestTickFast, bestTickRatio, gunCut);
            for (var k = 0; k < count; k++)
            {
                var gunGain = MathF.Pow(10f, -Reduction(gunEnv[k], -16f, 2f / 3f) / 20f);
                var y = other[k] + footBand[k] * footGain + tickBand[k] * tickGain + gunBand[k] * gunGain;
                var mag = MathF.Abs(y);
                if (mag > ceiling) y = MathF.CopySign(ceiling + MathF.Tanh((mag - ceiling) / 0.05f) * 0.05f, y);
                ears[k][i] = y;
            }
        }
    }

    /// Nova Pro only. The weight band holds like a drum hit and the tick stays short. Both are lifted
    /// only while they are a hit, by one gain for every ear. A steady tone is not lifted.
    /// A gun is trimmed only on a front ear, and not at all while a step is the hit, so the step is not silenced.
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
        var weightR = 1f - MathF.Exp(-1f / (rate * 0.080f));
        var slowA = 1f - MathF.Exp(-1f / (rate * 0.080f));
        var slowR = 1f - MathF.Exp(-1f / (rate * 0.400f));
        var gunAttack = 1f - MathF.Exp(-1f / (rate * 0.003f));
        var gunRelease = 1f - MathF.Exp(-1f / (rate * 0.122f));
        var peakA = 1f - MathF.Exp(-1f / (rate * 0.0004f));
        var peakR = 1f - MathF.Exp(-1f / (rate * 0.040f));
        var duckDown = 1f - MathF.Exp(-1f / (rate * 0.0015f));
        var duckUp = 1f - MathF.Exp(-1f / (rate * 0.012f));
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
        var shotEnv = new float[count];
        var shotSlow = new float[count];
        var shotPeak = new float[count];
        var weightBand = new float[count];
        var tickBand = new float[count];
        var gunBand = new float[count];
        var other = new float[count];
        const float ceiling = 0.8912509f;
        var own = 1f;
        var hold = 0;
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
                Follow(ref weightFast[k], MathF.Abs(weightBand[k]), fastA, weightR);
                Follow(ref weightSlow[k], MathF.Abs(weightBand[k]), slowA, slowR);
                Follow(ref tickFast[k], MathF.Abs(tickBand[k]), fastA, fastR);
                Follow(ref tickSlow[k], MathF.Abs(tickBand[k]), slowA, slowR);
                Follow(ref gunEnv[k], MathF.Abs(gunBand[k]), gunAttack, gunRelease);
                Follow(ref shotEnv[k], MathF.Abs(x), gunAttack, gunRelease);
                Follow(ref shotSlow[k], MathF.Abs(x), slowA, slowR);
                Follow(ref shotPeak[k], MathF.Abs(x), peakA, peakR);
                Consider(weightFast[k], weightSlow[k], ref bestWeightFast, ref bestWeightRatio);
                Consider(tickFast[k], tickSlow[k], ref bestTickFast, ref bestTickRatio);
            }
            var weightGain = TransientGain(bestWeightFast, bestWeightRatio, maxWeight);
            var tickGain = TransientGain(bestTickFast, bestTickRatio, maxTick);
            var step = weightGain > tickGain ? weightGain : tickGain;
            var crack = 1f;
            var gmax = 0f;
            var fmax = 0f;
            var tmax = 0f;
            for (var ear = 0; ear < count && ear < 8; ear++)
            {
                if (ear < 3)
                {
                    var c = shotEnv[ear] / (shotSlow[ear] + 1e-5f);
                    if (c > crack) crack = c;
                }
                if (gunEnv[ear] > gmax) gmax = gunEnv[ear];
                if (weightFast[ear] > fmax) fmax = weightFast[ear];
                if (tickFast[ear] > tmax) tmax = tickFast[ear];
            }
            var gunish = gmax > fmax * 1.35f && gmax > tmax * 1.35f && gmax > 0.0015f;
            if (gunish && crack >= 1.8f)
            {
                weightGain = 1f;
                tickGain = 1f;
                step = 1f;
            }
            var who = WhoFired(count > 0 ? shotPeak[0] : 0f, count > 1 ? shotPeak[1] : 0f, count > 2 ? shotPeak[2] : 0f, count, crack, gunish, ref hold);
            var target = who == 1 ? 0.10f : 1f;
            own += (target - own) * (target < own ? duckDown : duckUp);
            var body = own;
            for (var k = 0; k < count; k++)
            {
                var gunGain = 1f;
                if (who == 1) gunGain = body;
                else if (who == 2) gunGain = 2f;
                else if ((count <= 2 || k < 3) && !(step >= 1.15f && !gunish))
                    gunGain = MathF.Pow(10f, -Reduction(gunEnv[k], -16f, 2f / 3f) / 20f);
                var y = (other[k] + weightBand[k] * weightGain + tickBand[k] * tickGain) * body + gunBand[k] * gunGain;
                var mag = MathF.Abs(y);
                if (mag > ceiling) y = MathF.CopySign(ceiling + MathF.Tanh((mag - ceiling) / 0.05f) * 0.05f, y);
                ears[k][i] = y;
            }
        }
    }

    /// 1 = your gun or a close teammate (loud, both ears). 2 = an enemy shot. 0 = leave it.
    static int WhoFired(float left, float right, float centre, int ears, float crack, bool gunish, ref int hold)
    {
        if (crack < 1.8f || !gunish)
        {
            hold = 0;
            return 0;
        }
        if (ears < 2) right = left;
        if (ears < 3) centre = 0f;
        var side = left > right ? left : right;
        var hi = centre > side ? centre : side;
        var bal = (left < right ? left : right) / (side + 1e-8f);
        if (hi > 0.08f && bal > 0.55f) hold = 1;
        if (hold == 1 && bal > 0.55f) return 1;
        if (hi > 0.004f && hi <= 0.08f) return 2;
        if (hi > 0.02f && bal < 0.45f) return 2;
        return 0;
    }

    /// <summary>
    /// Cinema loudness. The same math as plugin/tune.c. Amount under 0.001 does not touch a sample.
    /// Quiet stretches rise. A hot stretch falls. Hiss stays. A shot louder than the bed is not lifted.
    /// There is no delay.
    /// </summary>
    public static void ApplyLoud(float[][] ears, int sampleRate, float amount)
    {
        if (amount < 0.001f || ears.Length == 0) return;
        if (amount > 1f) amount = 1f;
        var count = ears.Length > 8 ? 8 : ears.Length;
        var length = ears.Min(e => e.Length);
        var rate = (float)Rate(sampleRate);
        var hp = MathF.Exp(-2f * 3.14159274f * 70f / rate);
        var detUp = 1f - MathF.Exp(-1f / (rate * 0.180f));
        var detDown = 1f - MathF.Exp(-1f / (rate * 0.400f));
        var gUp = 1f - MathF.Exp(-1f / (rate * 0.550f));
        var gDown = 1f - MathF.Exp(-1f / (rate * 0.040f));
        var limR = 1f - MathF.Exp(-1f / (rate * 0.080f));
        var holdLen = (int)(rate * 0.045f);
        if (holdLen < 1) holdLen = 1;
        var target = -24f + 6f * amount;
        var z = new float[count];
        var det = 0f;
        var gainDb = 0f;
        var lim = 1f;
        var hold = 0;
        const float ceiling = 0.8912509f;
        for (var i = 0; i < length; i++)
        {
            var p = 0f;
            var used = 0;
            for (var ch = 0; ch < count; ch++)
            {
                var x = ears[ch][i];
                var zz = hp * z[ch] + (1f - hp) * x;
                if (MathF.Abs(zz) < 1e-20f) zz = 0f;
                z[ch] = zz;
                var h = x - zz;
                p += h * h;
                used++;
            }
            if (used > 1) p /= used;
            var slow = det;
            if (slow > 1e-12f && p > slow * 6f && p > 0.01f)
            {
                hold = holdLen;
                if (gainDb > 0f) gainDb = 0f;
            }
            {
                var tracked = p;
                if (slow > 1e-12f && tracked > slow * 8f) tracked = slow * 8f;
                var coef = tracked > slow ? detUp : detDown;
                slow += (tracked - slow) * coef;
                if (slow < 1e-20f) slow = 0f;
                det = slow;
            }
            var level = 10f * MathF.Log10(slow + 1e-20f);
            var want = (target - level) * 0.72f;
            if (want < -10f) want = -10f;
            if (want > 12f) want = 12f;
            var gate = (level + 66f) / 10f;
            if (gate < 0f) gate = 0f;
            else if (gate > 1f) gate = 1f;
            gate = gate * gate * (3f - 2f * gate);
            want *= gate;
            if (hold > 0)
            {
                want = gainDb < 0f ? gainDb : 0f;
                hold--;
            }
            gainDb += (want - gainDb) * (want > gainDb ? gUp : gDown);
            var g = MathF.Pow(10f, gainDb / 20f);
            var pk = 0f;
            for (var ch = 0; ch < count; ch++)
            {
                var y = ears[ch][i] * g;
                ears[ch][i] = y;
                var ay = MathF.Abs(y);
                if (ay > pk) pk = ay;
            }
            var need = pk > ceiling ? ceiling / pk : 1f;
            if (need < lim) lim = need;
            else lim += (1f - lim) * limR;
            for (var ch = 0; ch < count; ch++)
            {
                var y = ears[ch][i] * lim;
                var mag = MathF.Abs(y);
                if (mag > ceiling) y = MathF.CopySign(ceiling + MathF.Tanh((mag - ceiling) / 0.05f) * 0.05f, y);
                ears[ch][i] = y;
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
        var db = TransientDb(fast, ratio, maxDb);
        if (db <= 0f) return 1f;
        return MathF.Pow(10f, db / 20f);
    }

    // Up to +9 dB while the tick is a hit. Fades out as the loudest gun trim reaches 4 dB.
    static float ClassicTickGain(float fast, float ratio, float gunCutDb)
    {
        var guard = 1f - gunCutDb / 4f;
        if (guard <= 0f) return 1f;
        if (guard > 1f) guard = 1f;
        var db = TransientDb(fast, ratio, 9f) * guard;
        if (db <= 0f) return 1f;
        return MathF.Pow(10f, db / 20f);
    }

    static float TransientDb(float fast, float ratio, float maxDb)
    {
        if (maxDb <= 0.05f || fast < 1e-6f || ratio < 1.15f) return 0f;
        var novelty = 20f * MathF.Log10(ratio);
        if (novelty > 18f) novelty = 18f;
        var edge = novelty / 12f;
        if (edge > 1f) edge = 1f;
        var level = 20f * MathF.Log10(fast + 1e-8f);
        var quiet = (-2f - level) / 22f;
        if (quiet < 0.65f) quiet = 0.65f;
        if (quiet > 1f) quiet = 1f;
        var db = maxDb * quiet * edge;
        if (db <= 0f) return 0f;
        return db;
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
