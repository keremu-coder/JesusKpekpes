namespace JesusKpekpes;

public sealed class Engine
{
    public const int L = 0, R = 1, C = 2, Lfe = 3, Sl = 4, Sr = 5, Rl = 6, Rr = 7;
    public const int BlockFrames = 128;
    public const int Rate = 48000;
    public const string DeviceName = "Jesus Kpekpes";

    readonly Biquad[][] _correction;
    readonly Biquad[] _step;
    readonly Biquad[] _body;
    readonly Biquad[] _splash;
    readonly float[] _bandFast;
    readonly float[] _bandSlow;
    readonly float[] _fullFast;
    readonly float[] _fullSlow;
    readonly float[] _drop = new float[4];
    readonly float[] _hit = new float[4];
    readonly float _fastCoef;
    readonly float _bandSlowCoef;
    readonly float _fullSlowCoef;
    readonly float _frontAttack;
    readonly float _frontRelease;
    readonly float _dropDecay;
    float _frontEnv;
    float _frontGain = 1;
    public float Loudness { get; set; } = 1f;
    public float SurroundQuiet { get; set; } = 1f;

    static readonly int[] Surround = [Sl, Sr, Rl, Rr];

    public Engine(int sampleRate)
    {
        _step = [Bandpass(3400, 1.2f, sampleRate), Bandpass(3400, 1.2f, sampleRate), Bandpass(3400, 1.2f, sampleRate), Bandpass(3400, 1.2f, sampleRate)];
        _body = [Bandpass(280, 0.8f, sampleRate), Bandpass(280, 0.8f, sampleRate), Bandpass(280, 0.8f, sampleRate), Bandpass(280, 0.8f, sampleRate)];
        _splash = [Bandpass(4800, 1.4f, sampleRate), Bandpass(4800, 1.4f, sampleRate), Bandpass(4800, 1.4f, sampleRate), Bandpass(4800, 1.4f, sampleRate)];
        _bandFast = new float[4];
        _bandSlow = new float[4];
        _fullFast = new float[4];
        _fullSlow = new float[4];
        _fastCoef = 1 - MathF.Exp(-1f / (sampleRate * 0.008f));
        _bandSlowCoef = 1 - MathF.Exp(-1f / (sampleRate * 0.08f));
        _fullSlowCoef = 1 - MathF.Exp(-1f / (sampleRate * 0.18f));
        _frontAttack = 1 - MathF.Exp(-1f / (sampleRate * 0.003f));
        _frontRelease = 1 - MathF.Exp(-1f / (sampleRate * 0.08f));
        _dropDecay = MathF.Exp(-1f / (sampleRate * 0.022f));
        (float Hz, float Db, float Q)[] curve = [(80f, -4.5f, 0.9f), (200f, 4.5f, 0.8f), (3500f, 4.5f, 1.2f), (7000f, 2.5f, 1.1f)];
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

            ch[C] *= 0.35f;
            var frontPeak = MathF.Max(MathF.Abs(ch[L]), MathF.Abs(ch[R]));
            var frontCoef = frontPeak > _frontEnv ? _frontAttack : _frontRelease;
            _frontEnv += (frontPeak - _frontEnv) * frontCoef;
            var hold = _frontEnv > 0.18f ? 0.28f : 1f;
            _frontGain += (hold - _frontGain) * _frontAttack;
            ch[L] *= _frontGain;
            ch[R] *= _frontGain;

            for (var s = 0; s < Surround.Length; s++)
            {
                var c = Surround[s];
                var full = MathF.Abs(ch[c]);
                _fullFast[s] += (full - _fullFast[s]) * _fastCoef;
                _fullSlow[s] += (full - _fullSlow[s]) * _fullSlowCoef;
                if (_fullFast[s] < _fullSlow[s] * 1.25f) ch[c] *= 1f - SurroundQuiet * 0.78f;
                var body = _body[s].Tick(ch[c]);
                var band = _step[s].Tick(ch[c]);
                var splash = _splash[s].Tick(ch[c]);
                var mag = MathF.Abs(band);
                _bandFast[s] += (mag - _bandFast[s]) * _fastCoef;
                _bandSlow[s] += (mag - _bandSlow[s]) * _bandSlowCoef;
                var rise = _bandFast[s] / (_bandSlow[s] + 1e-5f);
                var lift = Math.Clamp((rise - 1.35f) / 1.4f, 0, 1);
                if (lift > _hit[s] && lift > 0.35f) _drop[s] = body * 1.4f + splash * 0.6f;
                _drop[s] *= _dropDecay;
                _hit[s] = lift;
                ch[c] += (body * 1.3f + band * 1.1f + splash * 0.5f) * lift * Loudness * 3.2f;
                ch[c] += _drop[s] * Loudness * 2.2f;
            }

            left[i] = Math.Clamp(0.72f * ch[L] + 0.2f * ch[C] + 1.15f * ch[Sl] + 0.8f * ch[Rl], -1, 1);
            right[i] = Math.Clamp(0.72f * ch[R] + 0.2f * ch[C] + 1.15f * ch[Sr] + 0.8f * ch[Rr], -1, 1);
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
