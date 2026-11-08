namespace JesusKpekpes;

public sealed class Engine
{
    public const int L = 0, R = 1, C = 2, Lfe = 3, Sl = 4, Sr = 5, Rl = 6, Rr = 7;

    readonly int _sampleRate;
    readonly Biquad[] _step;
    readonly float[] _fast;
    readonly float[] _slow;
    readonly float _fastCoef;
    readonly float _slowCoef;
    readonly float _frontCoef;
    readonly float _frontRelease;
    Biquad[][] _correction = [];
    float _frontEnv;
    float _frontGain = 1;

    static readonly int[] Surround = [Sl, Sr, Rl, Rr];
    static readonly int[] Front = [L, R, C];

    public Engine(int sampleRate)
    {
        _sampleRate = sampleRate;
        _step = [Bandpass(3200, 1.4f), Bandpass(3200, 1.4f), Bandpass(3200, 1.4f), Bandpass(3200, 1.4f)];
        _fast = new float[4];
        _slow = new float[4];
        _fastCoef = 1 - MathF.Exp(-1f / (sampleRate * 0.008f));
        _slowCoef = 1 - MathF.Exp(-1f / (sampleRate * 0.08f));
        _frontCoef = 1 - MathF.Exp(-1f / (sampleRate * 0.004f));
        _frontRelease = 1 - MathF.Exp(-1f / (sampleRate * 0.1f));
    }

    public void SetCorrection(IReadOnlyList<(float Hz, float Db, float Q)> bands)
    {
        _correction = new Biquad[8][];
        for (var c = 0; c < 8; c++)
        {
            _correction[c] = new Biquad[bands.Count];
            for (var i = 0; i < bands.Count; i++)
            {
                var band = bands[i];
                _correction[c][i] = Peaking(band.Hz, band.Db, band.Q);
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
                if (c < _correction.Length)
                {
                    foreach (var filter in _correction[c]) x = filter.Tick(x);
                }
                ch[c] = x;
            }

            var frontPeak = 0f;
            foreach (var c in Front) frontPeak = MathF.Max(frontPeak, MathF.Abs(ch[c]));
            var coef = frontPeak > _frontEnv ? _frontCoef : _frontRelease;
            _frontEnv += (frontPeak - _frontEnv) * coef;
            var hold = _frontEnv > 0.35f ? 0.62f : 1f;
            _frontGain += (hold - _frontGain) * _frontCoef;
            foreach (var c in Front) ch[c] *= _frontGain;

            for (var s = 0; s < Surround.Length; s++)
            {
                var c = Surround[s];
                var band = _step[s].Tick(ch[c]);
                var mag = MathF.Abs(band);
                _fast[s] += (mag - _fast[s]) * _fastCoef;
                _slow[s] += (_fast[s] - _slow[s]) * _slowCoef;
                var rise = _fast[s] / (_slow[s] + 1e-5f);
                var lift = Math.Clamp((rise - 1.7f) / 2f, 0, 1);
                ch[c] += band * lift * 1.8f;
            }

            left[i] = ch[L] + 0.5f * ch[C] + 0.7f * ch[Sl] + 0.5f * ch[Rl];
            right[i] = ch[R] + 0.5f * ch[C] + 0.7f * ch[Sr] + 0.5f * ch[Rr];
        }
    }

    Biquad Peaking(float hz, float db, float q)
    {
        var a = MathF.Pow(10, db / 40);
        var w0 = 2 * MathF.PI * hz / _sampleRate;
        var cos = MathF.Cos(w0);
        var alpha = MathF.Sin(w0) / (2 * q);
        var a0 = 1 + alpha / a;
        return new Biquad((1 + alpha * a) / a0, (-2 * cos) / a0, (1 - alpha * a) / a0, (-2 * cos) / a0, (1 - alpha / a) / a0);
    }

    Biquad Bandpass(float hz, float q)
    {
        var w0 = 2 * MathF.PI * hz / _sampleRate;
        var cos = MathF.Cos(w0);
        var alpha = MathF.Sin(w0) / (2 * q);
        var a0 = 1 + alpha;
        return new Biquad(alpha / a0, 0, -alpha / a0, (-2 * cos) / a0, (1 - alpha) / a0);
    }

    sealed class Biquad(float b0, float b1, float b2, float a1, float a2)
    {
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
