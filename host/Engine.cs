namespace JesusKpekpes;

public sealed class Engine
{
    public const int L = 0, R = 1, C = 2, Lfe = 3, Sl = 4, Sr = 5, Rl = 6, Rr = 7;
    public const int BlockFrames = 128;
    public const int Rate = 48000;
    public const string DeviceName = "Jesus Kpekpes";

    readonly Biquad[][] _correction;
    readonly Biquad[] _step;
    readonly float[] _bandFast;
    readonly float[] _bandSlow;
    readonly float[] _fullFast;
    readonly float[] _fullSlow;
    readonly float _fastCoef;
    readonly float _bandSlowCoef;
    readonly float _fullSlowCoef;
    readonly float _frontAttack;
    readonly float _frontRelease;
    readonly float _loudAttack;
    readonly float _loudRelease;
    float _frontEnv;
    float _frontGain = 1;
    float _loudEnv;
    float _loudGain = 1;

    static readonly int[] Surround = [Sl, Sr, Rl, Rr];

    public Engine(int sampleRate)
    {
        _step = [Bandpass(3400, 1.4f, sampleRate), Bandpass(3400, 1.4f, sampleRate), Bandpass(3400, 1.4f, sampleRate), Bandpass(3400, 1.4f, sampleRate)];
        _bandFast = new float[4];
        _bandSlow = new float[4];
        _fullFast = new float[4];
        _fullSlow = new float[4];
        _fastCoef = 1 - MathF.Exp(-1f / (sampleRate * 0.008f));
        _bandSlowCoef = 1 - MathF.Exp(-1f / (sampleRate * 0.08f));
        _fullSlowCoef = 1 - MathF.Exp(-1f / (sampleRate * 0.18f));
        _frontAttack = 1 - MathF.Exp(-1f / (sampleRate * 0.003f));
        _frontRelease = 1 - MathF.Exp(-1f / (sampleRate * 0.08f));
        _loudAttack = 1 - MathF.Exp(-1f / (sampleRate * 0.005f));
        _loudRelease = 1 - MathF.Exp(-1f / (sampleRate * 0.12f));
        (float Hz, float Db, float Q)[] curve = [(80f, -3.5f, 0.9f), (200f, 3.5f, 0.8f), (3500f, 3f, 1.3f), (7000f, 2f, 1.1f)];
        _correction = new Biquad[8][];
        for (var c = 0; c < 8; c++)
        {
            _correction[c] = new Biquad[curve.Length];
            for (var i = 0; i < curve.Length; i++)
            {
                var band = curve[i];
                _correction[c][i] = Peaking(band.Hz, band.Db, band.Q, sampleRate);
            }
        }
    }

    public void SetCorrection(IReadOnlyList<(float Hz, float Db, float Q)> bands)
    {
        var sampleRate = _correction[0][0].SampleRate;
        for (var c = 0; c < 8; c++)
        {
            _correction[c] = new Biquad[bands.Count];
            for (var i = 0; i < bands.Count; i++)
            {
                var band = bands[i];
                _correction[c][i] = Peaking(band.Hz, band.Db, band.Q, sampleRate);
            }
        }
    }

    public void Process(float[][] input, float[] left, float[] right)
    {
        var n = input[0].Length;
        for (var i = 0; i < n; i++)
        {
            Span<float> ch = stackalloc float[8];
            for (var c = 0; c < 8; c++)
            {
                var x = c < input.Length ? input[c][i] : 0;
                foreach (var filter in _correction[c]) x = filter.Tick(x);
                ch[c] = x;
            }

            ch[C] *= 0.45f;
            var frontPeak = MathF.Max(MathF.Abs(ch[L]), MathF.Abs(ch[R]));
            var frontCoef = frontPeak > _frontEnv ? _frontAttack : _frontRelease;
            _frontEnv += (frontPeak - _frontEnv) * frontCoef;
            var hold = _frontEnv > 0.28f ? 0.45f : 1f;
            _frontGain += (hold - _frontGain) * _frontAttack;
            ch[L] *= _frontGain;
            ch[R] *= _frontGain;

            for (var s = 0; s < Surround.Length; s++)
            {
                var c = Surround[s];
                var full = MathF.Abs(ch[c]);
                _fullFast[s] += (full - _fullFast[s]) * _fastCoef;
                _fullSlow[s] += (full - _fullSlow[s]) * _fullSlowCoef;
                if (_fullFast[s] < _fullSlow[s] * 1.35f) ch[c] *= 0.42f;
                var band = _step[s].Tick(ch[c]);
                var mag = MathF.Abs(band);
                _bandFast[s] += (mag - _bandFast[s]) * _fastCoef;
                _bandSlow[s] += (mag - _bandSlow[s]) * _bandSlowCoef;
                var rise = _bandFast[s] / (_bandSlow[s] + 1e-5f);
                var lift = Math.Clamp((rise - 1.6f) / 2f, 0, 1);
                ch[c] += band * lift * 2.2f;
            }

            left[i] = 0.8f * ch[L] + 0.28f * ch[C] + 0.95f * ch[Sl] + 0.7f * ch[Rl];
            right[i] = 0.8f * ch[R] + 0.28f * ch[C] + 0.95f * ch[Sr] + 0.7f * ch[Rr];
            var peak = MathF.Max(MathF.Abs(left[i]), MathF.Abs(right[i]));
            var follow = peak > _loudEnv ? _loudAttack : _loudRelease;
            _loudEnv += (peak - _loudEnv) * follow;
            var target = _loudEnv < 0.02f ? 2.4f : _loudEnv < 0.1f ? 1.8f : 1f;
            _loudGain += (target - _loudGain) * _loudAttack;
            left[i] = Math.Clamp(left[i] * _loudGain, -1, 1);
            right[i] = Math.Clamp(right[i] * _loudGain, -1, 1);
        }
    }

    static Biquad Peaking(float hz, float db, float q, float sampleRate)
    {
        var a = MathF.Pow(10, db / 40);
        var w0 = 2 * MathF.PI * hz / sampleRate;
        var cos = MathF.Cos(w0);
        var alpha = MathF.Sin(w0) / (2 * q);
        var a0 = 1 + alpha / a;
        return new Biquad(sampleRate, (1 + alpha * a) / a0, (-2 * cos) / a0, (1 - alpha * a) / a0, (-2 * cos) / a0, (1 - alpha / a) / a0);
    }

    static Biquad Bandpass(float hz, float q, float sampleRate)
    {
        var w0 = 2 * MathF.PI * hz / sampleRate;
        var cos = MathF.Cos(w0);
        var alpha = MathF.Sin(w0) / (2 * q);
        var a0 = 1 + alpha;
        return new Biquad(sampleRate, alpha / a0, 0, -alpha / a0, (-2 * cos) / a0, (1 - alpha) / a0);
    }

    sealed class Biquad(float sampleRate, float b0, float b1, float b2, float a1, float a2)
    {
        public float SampleRate { get; } = sampleRate;
        float _x1, _x2, _y1, _y2;
        public float Tick(float x)
        {
            var y = b0 * x + b1 * _x1 + b2 * _x2 - a1 * _y1 - a2 * _y2;
            _x2 = _x1;
            _x1 = x;
            _y2 = _y1;
            _y1 = y;
            return y;
        }
    }
}
