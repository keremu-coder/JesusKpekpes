using System.Globalization;

namespace JesusKpekpes;

enum Preset { Drop, Well }

enum Route { Stereo, Surround }

sealed record MixPlan(Preset Preset, Route Route, int Hit, int Quiet, string Device);

static class Mix
{
    public const string IncludeLine = "Include: jesus-kpekpes\\switch.txt";

    public static string Config(MixPlan plan)
    {
        var n = CultureInfo.InvariantCulture;
        var filters = Filters(plan);
        var preamp = PreampDb(filters, plan.Route);
        var text = new System.Text.StringBuilder();
        text.Append("Device: ").Append(DeviceName(plan.Device)).Append("\r\n");
        if (plan.Route == Route.Surround)
        {
            Write(text, n, "C", filters.Where(f => f.Channels == "C"));
            Write(text, n, "L R", filters.Where(f => f.Channels == "L R"));
            Write(text, n, "SL SR RL RR", filters.Where(f => f.Channels == "SL SR RL RR"));
            text.Append("Channel: LFE\r\n");
            text.Append("Preamp: ").Append(Lfe(plan).ToString("0.0", n)).Append(" dB\r\n");
            text.Append("Copy: L=L+0.7*SL+0.5*RL+0.7*C R=R+0.7*SR+0.5*RR+0.7*C\r\n");
            text.Append("Channel: C LFE SL SR RL RR\r\n");
            text.Append("Preamp: -120 dB\r\n");
        }
        else
        {
            Write(text, n, "L R", filters);
        }
        text.Append("Channel: all\r\n");
        text.Append("Preamp: ").Append(preamp.ToString("0.0", n)).Append(" dB\r\n");
        // Hand the scope back so nothing loaded after us (HeSuVi, Rara, the user's own lines) inherits it.
        text.Append("Device: all\r\n");
        text.Append("Channel: all\r\n");
        return text.ToString();
    }

    /// <summary>Equalizer APO matches devices by name without the brackets Windows adds, e.g. "Headphones Realtek High Definition Audio".</summary>
    public static string DeviceName(string friendly)
    {
        var plain = friendly.Replace("(", " ").Replace(")", " ");
        return string.Join(" ", plain.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>On: include our filter file by its full path, so it does not depend on how Equalizer APO resolves relative paths. Off: empty.</summary>
    public static string Switch(bool on, string? folder = null) =>
        on ? "Include: " + (folder is null ? "jesus-kpekpes" : folder) + "\\on.txt\r\n" : "";

    public static IReadOnlyList<string> Conflicts(string text)
    {
        var found = new List<string>();
        var lower = text.ToLowerInvariant();
        if (lower.Contains("hesuvi")) found.Add("HeSuVi");
        if (lower.Contains("rara")) found.Add("Rara");
        if (lower.Contains("peace")) found.Add("Peace");
        if (lower.Contains("vstplugin:")) found.Add("VST plugins");
        if (lower.Contains("convolution:")) found.Add("Convolution");
        return found;
    }

    public static void Process(MixPlan plan, float[] left, float[] right, float[]? sideLeft = null, float[]? sideRight = null)
    {
        var filters = Filters(plan);
        var preamp = MathF.Pow(10, PreampDb(filters, plan.Route) / 20f);
        var sr = 48000;
        if (plan.Route == Route.Stereo)
        {
            var chainL = Chain(filters, sr);
            var chainR = Chain(filters, sr);
            for (var i = 0; i < left.Length; i++)
            {
                var l = left[i];
                var r = right[i];
                foreach (var f in chainL) l = f.Tick(l);
                foreach (var f in chainR) r = f.Tick(r);
                left[i] = l * preamp;
                right[i] = r * preamp;
            }
            return;
        }

        var front = filters.Where(f => f.Channels == "L R").ToArray();
        var side = filters.Where(f => f.Channels == "SL SR RL RR").ToArray();
        var frontL = Chain(front, sr);
        var frontR = Chain(front, sr);
        var sideL = Chain(side, sr);
        var sideR = Chain(side, sr);
        for (var i = 0; i < left.Length; i++)
        {
            var l = left[i];
            var r = right[i];
            var sl = sideLeft is null ? 0 : sideLeft[i];
            var srch = sideRight is null ? 0 : sideRight[i];
            foreach (var f in frontL) l = f.Tick(l);
            foreach (var f in frontR) r = f.Tick(r);
            foreach (var f in sideL) sl = f.Tick(sl);
            foreach (var f in sideR) srch = f.Tick(srch);
            l = (l + 0.7f * sl) * preamp;
            r = (r + 0.7f * srch) * preamp;
            left[i] = l;
            right[i] = r;
        }
    }

    public static float PreampDb(MixPlan plan) => PreampDb(Filters(plan), plan.Route);

    static float PreampDb(Band[] filters, Route route)
    {
        var worst = 0f;
        for (var hz = 40f; hz <= 16000f; hz *= 1.06f)
        {
            var db = 0f;
            foreach (var band in filters.Where(f => f.Channels is "L R" or "SL SR RL RR"))
                db += ResponseDb(band, hz);
            if (route == Route.Surround) db += 6.8f;
            if (db > worst) worst = db;
        }
        return worst <= 0.05f ? 0 : -(worst + 0.5f);
    }

    static Band[] Filters(MixPlan plan)
    {
        var hit = Math.Clamp(plan.Hit, 0, 100) / 100f;
        var quiet = Math.Clamp(plan.Quiet, 0, 100) / 100f;
        if (plan.Route == Route.Surround)
        {
            return
            [
                new("C", Shelf.None, 0, -(quiet * 8f), 0.7f),
                new("L R", Shelf.Low, 140, -(quiet * 6f), 0.7f),
                new("SL SR RL RR", Shelf.Low, 180, -(quiet * 6f), 0.7f),
                new("SL SR RL RR", Shelf.None, 3400, hit * 6f, 1.2f),
                new("SL SR RL RR", Shelf.None, 280, hit * 2f, 0.8f),
            ];
        }
        if (plan.Preset == Preset.Well)
        {
            return
            [
                new("L R", Shelf.Low, 180, -(quiet * 6f), 0.7f),
                new("L R", Shelf.None, 450, -(quiet * 3f), 1f),
                new("L R", Shelf.None, 3400, hit * 7f, 1.2f),
                new("L R", Shelf.None, 280, hit * 1.5f, 0.8f),
            ];
        }
        return
        [
            new("L R", Shelf.Low, 140, -(quiet * 4f), 0.7f),
            new("L R", Shelf.None, 280, hit * 2f, 0.8f),
            new("L R", Shelf.None, 3400, hit * 6f, 1.2f),
            new("L R", Shelf.None, 4800, hit * 2f, 1.6f),
        ];
    }

    static float Lfe(MixPlan plan) => -(plan.Quiet / 100f * 18f);

    static void Write(System.Text.StringBuilder text, CultureInfo n, string channels, IEnumerable<Band> filters)
    {
        var list = filters.ToArray();
        if (list.Length == 0) return;
        text.Append("Channel: ").Append(channels).Append("\r\n");
        foreach (var band in list)
        {
            if (band.Kind == Shelf.None && band.Hz <= 0)
            {
                text.Append("Preamp: ").Append(band.Db.ToString("0.0", n)).Append(" dB\r\n");
                continue;
            }
            var kind = band.Kind == Shelf.Low ? "LSC" : "PK";
            text.Append("Filter: ON ").Append(kind)
                .Append(" Fc ").Append(band.Hz.ToString("0", n))
                .Append(" Hz Gain ").Append(band.Db.ToString("0.0", n))
                .Append(" dB Q ").Append(band.Q.ToString("0.0", n))
                .Append("\r\n");
        }
    }

    static float ResponseDb(Band band, float hz)
    {
        if (Math.Abs(band.Db) < 0.05f) return 0;
        var coeff = band.Kind == Shelf.Low ? LowCoeff(band.Hz, band.Db, band.Q, 48000) : PeakCoeff(band.Hz, band.Db, band.Q, 48000);
        var w = 2 * MathF.PI * hz / 48000f;
        var z1 = new System.Numerics.Complex(MathF.Cos(w), -MathF.Sin(w));
        var z2 = z1 * z1;
        var h = (coeff.B0 + coeff.B1 * z1 + coeff.B2 * z2) / (1 + coeff.A1 * z1 + coeff.A2 * z2);
        return 20 * MathF.Log10(Math.Max(1e-8f, (float)h.Magnitude));
    }

    static Biquad[] Chain(IEnumerable<Band> bands, int sampleRate) =>
        bands.Where(b => Math.Abs(b.Db) >= 0.05f && b.Hz > 0)
            .Select(b => b.Kind == Shelf.Low
                ? MakeShelf(b.Hz, b.Db, b.Q, sampleRate)
                : MakePeak(b.Hz, b.Db, b.Q, sampleRate))
            .ToArray();

    static Biquad MakePeak(float hz, float db, float q, int sampleRate)
    {
        var c = PeakCoeff(hz, db, q, sampleRate);
        return new Biquad(c.B0, c.B1, c.B2, c.A1, c.A2);
    }

    static Biquad MakeShelf(float hz, float db, float q, int sampleRate)
    {
        var c = LowCoeff(hz, db, q, sampleRate);
        return new Biquad(c.B0, c.B1, c.B2, c.A1, c.A2);
    }

    static Coeff PeakCoeff(float hz, float db, float q, int sampleRate)
    {
        var a = MathF.Pow(10, db / 40);
        var w0 = 2 * MathF.PI * hz / sampleRate;
        var cos = MathF.Cos(w0);
        var alpha = MathF.Sin(w0) / (2 * q);
        var a0 = 1 + alpha / a;
        return new((1 + alpha * a) / a0, (-2 * cos) / a0, (1 - alpha * a) / a0, (-2 * cos) / a0, (1 - alpha / a) / a0);
    }

    static Coeff LowCoeff(float hz, float db, float q, int sampleRate)
    {
        var a = MathF.Pow(10, db / 40);
        var w0 = 2 * MathF.PI * hz / sampleRate;
        var cos = MathF.Cos(w0);
        var sin = MathF.Sin(w0);
        var alpha = sin / (2 * q);
        var beta = 2 * MathF.Sqrt(a) * alpha;
        var b0 = a * ((a + 1) - (a - 1) * cos + beta);
        var b1 = 2 * a * ((a - 1) - (a + 1) * cos);
        var b2 = a * ((a + 1) - (a - 1) * cos - beta);
        var a0 = (a + 1) + (a - 1) * cos + beta;
        var a1 = -2 * ((a - 1) + (a + 1) * cos);
        var a2 = (a + 1) + (a - 1) * cos - beta;
        return new(b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0);
    }

    enum Shelf { None, Low }

    sealed record Band(string Channels, Shelf Kind, float Hz, float Db, float Q);
    readonly record struct Coeff(float B0, float B1, float B2, float A1, float A2);

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
