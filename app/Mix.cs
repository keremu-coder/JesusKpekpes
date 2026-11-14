using System.Globalization;

namespace JesusKpekpes;

enum Preset { Drop, Well, Nova }

enum Route { Stereo, Surround }

enum Level { Safe, Balanced, Loud }

sealed record MixPlan(Preset Preset, Route Route, int Hit, int Quiet, string Device, Level Level = Level.Safe, bool Boost = false, int Power = 75);

static class Mix
{
    public const string IncludeLine = "Include: jesus-kpekpes\\switch.txt";

    public static string Switch(bool on, string? onFile = null)
    {
        if (!on || string.IsNullOrWhiteSpace(onFile)) return "";
        return "Include: " + onFile + "\r\n";
    }

    public static string RootInclude(string switchFile) => "Include: " + switchFile + "\r\n";

    public static string Config(MixPlan plan, string? plugin = null)
    {
        var n = CultureInfo.InvariantCulture;
        var filters = Filters(plan);
        var nova = plan.Preset == Preset.Nova;
        var pluginOn = !string.IsNullOrWhiteSpace(plugin);
        var cinema = pluginOn ? Cinema(plan.Level) : 0f;
        // Safe keeps today's preamp. Balanced and Loud hold the ceiling in the plugin, so a later preamp would only undo it.
        // Surround still leaves a little room, because the 7.1 fold happens after the plugin.
        float preamp;
        if (!pluginOn || (nova && cinema <= 0f))
            preamp = PreampDb(filters, plan.Route, plan.Level, nova);
        else if (cinema <= 0f)
            preamp = plan.Level switch { Level.Balanced => 1.5f, Level.Loud => 3f, _ => 0f };
        else
            preamp = plan.Route == Route.Surround ? -3f : 0f;
        var text = new System.Text.StringBuilder();
        text.Append("Device: ").Append(DeviceSelector(plan.Device)).Append("\r\n");
        if (plan.Route == Route.Surround && nova)
            text.Append("# Nova Pro fold. Left stays left. Centre is split equally. Rear is quieter than beside you. The low speaker is not added.\r\n");
        else if (plan.Route == Route.Surround)
            text.Append("# 7.1 is untested. The centre speaker and LFE are muted after the fold. That fold level was not measured.\r\n");
        if (plan.Route == Route.Surround)
        {
            Write(text, n, "C", filters.Where(f => f.Channels == "C"));
            Write(text, n, "L R", filters.Where(f => f.Channels == "L R"));
            Write(text, n, "SL SR RL RR", filters.Where(f => f.Channels == "SL SR RL RR"));
            text.Append("Channel: LFE\r\n");
            text.Append("Preamp: ").Append(Lfe(plan).ToString("0.0", n)).Append(" dB\r\n");
        }
        else
        {
            Write(text, n, "L R", filters);
        }
        if (!string.IsNullOrWhiteSpace(plugin))
        {
            // The low speaker is background. Nova must not let it vote for the step lift.
            text.Append(nova && plan.Route == Route.Surround
                ? "Channel: L R C SL SR RL RR\r\n"
                : "Channel: all\r\n");
            text.Append("VSTPlugin: Library \"").Append(plugin).Append('"');
            var hit01 = Math.Clamp(plan.Hit, 0, 100) / 100f;
            if (nova)
                text.Append(" Nova 1 Hit ").Append(hit01.ToString("0.00", n));
            else
                text.Append(" Hit ").Append(hit01.ToString("0.00", n));
            // Boost step is off unless asked for. Off writes nothing, so the line is exactly what it was before.
            if (plan.Boost)
                text.Append(" Boost 1 Power ").Append((Math.Clamp(plan.Power, 0, 100) / 100f).ToString("0.00", n));
            if (cinema > 0f)
                text.Append(" Loud ").Append(cinema.ToString("0.00", n));
            text.Append("\r\n");
        }
        if (plan.Route == Route.Surround)
        {
            if (nova)
            {
                text.Append("Channel: all\r\n");
                text.Append(FoldNova(n));
            }
            else
                text.Append("Copy: L=L+0.5*C+0.25*SL+0.14*RL R=R+0.5*C+0.25*SR+0.14*RR\r\n");
            text.Append("Channel: C LFE SL SR RL RR\r\n");
            text.Append("Preamp: -120 dB\r\n");
        }
        text.Append("Channel: all\r\n");
        text.Append("Preamp: ").Append(preamp.ToString("0.0", n)).Append(" dB\r\n");
        text.Append("Device: all\r\n");
        return text.ToString();
    }

    public static IReadOnlyList<string> Conflicts(string text)
    {
        var found = new List<string>();
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var lower = line.ToLowerInvariant();
            if (lower.Contains("hesuvi")) Add(found, "HeSuVi");
            if (lower.Contains("raraloudness") || lower.Contains("rarasuppression") || lower.Contains("raraambience")
                || lower.Contains("raraaudio") || lower.Contains("rarafrequency") || lower.Contains("rara audio"))
                Add(found, "Rara");
            if (WholeWord(lower, "peace")) Add(found, "Peace");
            if (lower.Contains("convolution:")) Add(found, "Convolution");
            if (lower.Contains("vstplugin:") && !lower.Contains("jesuskpekpestune")) Add(found, "VST plugin");
        }
        return found;
    }

    static void Add(List<string> found, string name)
    {
        if (!found.Contains(name)) found.Add(name);
    }

    static bool WholeWord(string text, string word)
    {
        var at = 0;
        while ((at = text.IndexOf(word, at, StringComparison.Ordinal)) >= 0)
        {
            var before = at == 0 || !char.IsLetter(text[at - 1]);
            var after = at + word.Length >= text.Length || !char.IsLetter(text[at + word.Length]);
            if (before && after) return true;
            at += word.Length;
        }
        return false;
    }

    public static void Process(MixPlan plan, float[] left, float[] right, float[]? sideLeft = null, float[]? sideRight = null, float[]? center = null, float[]? rearLeft = null, float[]? rearRight = null, int sampleRate = 48000)
    {
        var filters = Filters(plan);
        var sr = sampleRate >= 1000 ? sampleRate : 48000;
        var nova = plan.Preset == Preset.Nova;
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
                left[i] = l;
                right[i] = r;
            }
            if (nova) TuneEngine.ShapeNova([left, right], sr, plan.Hit);
            else TuneEngine.CompressLinked([left, right], sr, plan.Hit);
            var stereoCinema = Cinema(plan.Level);
            if (stereoCinema > 0f) TuneEngine.ApplyLoud([left, right], sr, stereoCinema);
            return;
        }

        var front = filters.Where(f => f.Channels == "L R").ToArray();
        var side = filters.Where(f => f.Channels == "SL SR RL RR").ToArray();
        var centerBands = filters.Where(f => f.Channels == "C").ToArray();
        var frontL = Chain(front, sr);
        var frontR = Chain(front, sr);
        var sideL = Chain(side, sr);
        var sideR = Chain(side, sr);
        var rearL = Chain(side, sr);
        var rearR = Chain(side, sr);
        var centerChain = Chain(centerBands, sr);
        for (var i = 0; i < left.Length; i++)
        {
            var l = left[i];
            var r = right[i];
            var sl = sideLeft is null ? 0 : sideLeft[i];
            var srch = sideRight is null ? 0 : sideRight[i];
            var c = center is null ? 0 : center[i];
            var rl = rearLeft is null ? 0 : rearLeft[i];
            var rr = rearRight is null ? 0 : rearRight[i];
            foreach (var f in frontL) l = f.Tick(l);
            foreach (var f in frontR) r = f.Tick(r);
            foreach (var f in sideL) sl = f.Tick(sl);
            foreach (var f in sideR) srch = f.Tick(srch);
            foreach (var f in rearL) rl = f.Tick(rl);
            foreach (var f in rearR) rr = f.Tick(rr);
            foreach (var f in centerChain) c = f.Tick(c);
            left[i] = l;
            right[i] = r;
            if (sideLeft is not null) sideLeft[i] = sl;
            if (sideRight is not null) sideRight[i] = srch;
            if (center is not null) center[i] = c;
            if (rearLeft is not null) rearLeft[i] = rl;
            if (rearRight is not null) rearRight[i] = rr;
        }
        var ears = new[] { left, right, sideLeft, sideRight, center, rearLeft, rearRight }.OfType<float[]>().ToArray();
        if (nova) TuneEngine.ShapeNova(ears, sr, plan.Hit);
        else TuneEngine.CompressLinked(ears, sr, plan.Hit);
        var surroundCinema = Cinema(plan.Level);
        if (surroundCinema > 0f) TuneEngine.ApplyLoud(ears, sr, surroundCinema);
        if (nova)
        {
            for (var i = 0; i < left.Length; i++)
            {
                var sl = sideLeft is null ? 0 : sideLeft[i];
                var srch = sideRight is null ? 0 : sideRight[i];
                var c = center is null ? 0 : center[i];
                var rl = rearLeft is null ? 0 : rearLeft[i];
                var rr = rearRight is null ? 0 : rearRight[i];
                left[i] = left[i] + NovaCenter * c + NovaSide * sl + NovaRear * rl;
                right[i] = right[i] + NovaCenter * c + NovaSide * srch + NovaRear * rr;
            }
            return;
        }
        const float surroundBed = 0.31622776f;
        for (var i = 0; i < left.Length; i++)
        {
            var sl = sideLeft is null ? 0 : sideLeft[i] * surroundBed;
            var srch = sideRight is null ? 0 : sideRight[i] * surroundBed;
            var c = center is null ? 0 : center[i];
            var rl = rearLeft is null ? 0 : rearLeft[i] * surroundBed;
            var rr = rearRight is null ? 0 : rearRight[i] * surroundBed;
            left[i] = left[i] + 0.5f * c + 0.25f * sl + 0.14f * rl;
            right[i] = right[i] + 0.5f * c + 0.25f * srch + 0.14f * rr;
        }
    }

    public static bool DeviceMatches(string pattern, string deviceString)
    {
        var words = pattern.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length > 0 && words.All(word => deviceString.Contains(word, StringComparison.OrdinalIgnoreCase));
    }

    public static string DeviceSelector(string device)
    {
        var open = device.LastIndexOf('{');
        var close = device.LastIndexOf('}');
        if (open >= 0 && close > open) return device[open..(close + 1)];
        return device.Trim();
    }

    public static string StripInclude(string config)
    {
        var lines = config.Replace("\r\n", "\n").Split('\n');
        var kept = lines.Where(line => !line.Contains("jesus-kpekpes", StringComparison.OrdinalIgnoreCase));
        return string.Join("\r\n", kept).TrimEnd();
    }

    public static float PreampDb(MixPlan plan) => PreampDb(Filters(plan), plan.Route, plan.Level, plan.Preset == Preset.Nova);

    // 0 is Safe (the plugin line is unchanged). Balanced aims near -20 dB, Loud near -18.
    static float Cinema(Level level) => level switch { Level.Balanced => 0.67f, Level.Loud => 1f, _ => 0f };

    // Front plus centre, so a 7.1 Nova fold still has room when both speak at once.
    const float NovaCenter = 0.707f;
    const float NovaSide = 0.5f;
    const float NovaRear = 0.32f;

    static string FoldNova(CultureInfo n) =>
        string.Format(n, "Copy: L=L+{0:0.000}*C+{1:0.000}*SL+{2:0.000}*RL R=R+{3:0.000}*C+{4:0.000}*SR+{5:0.000}*RR\r\n",
            NovaCenter, NovaSide, NovaRear, NovaCenter, NovaSide, NovaRear);

    static float PreampDb(Band[] filters, Route route, Level level, bool nova)
    {
        var worst = -120f;
        for (var hz = 40f; hz <= 16000f; hz *= 1.06f)
        {
            var db = OutputDb(filters, route, hz, nova);
            if (db > worst) worst = db;
        }
        var safe = -(worst + 1f);
        var give = level switch { Level.Balanced => 2f, Level.Loud => 4f, _ => 0f };
        return MathF.Min(-0.5f, safe + give);
    }

    static float OutputDb(Band[] filters, Route route, float hz, bool nova)
    {
        float Mag(string channels)
        {
            var db = 0f;
            foreach (var band in filters)
            {
                if (band.Channels != channels || band.Hz <= 0) continue;
                db += ResponseDb(band, hz);
            }
            return MathF.Pow(10f, db / 20f);
        }

        if (route == Route.Stereo) return 20f * MathF.Log10(MathF.Max(1e-8f, Mag("L R")));
        var front = Mag("L R");
        if (nova)
        {
            var stacked = front * (1f + NovaCenter);
            return 20f * MathF.Log10(MathF.Max(1e-8f, stacked));
        }
        var side = Mag("SL SR RL RR") * 0.31622776f;
        var folded = front + 0.5f + (0.25f + 0.14f) * side;
        return 20f * MathF.Log10(MathF.Max(1e-8f, folded));
    }

    static Band[] Filters(MixPlan plan)
    {
        var hit = Math.Clamp(plan.Hit, 0, 100) / 100f;
        var quiet = Math.Clamp(plan.Quiet, 0, 100) / 100f;
        if (plan.Preset == Preset.Nova)
        {
            // Wired Nova Pro, small USB adapter treated as flat (Sonar and the GameDAC EQ off).
            // Measured on this headset: a bass bump near 140 Hz, a dip around 3-4 kHz, a peak at 8 kHz.
            // The tick is put on the dip, not on the peak. The 8 kHz cut does not follow the hit slider.
            var bands = new List<Band>();
            bands.AddRange(NovaBands("L R", hit, quiet));
            if (plan.Route == Route.Surround)
            {
                bands.AddRange(NovaBands("C", hit, quiet));
                bands.AddRange(NovaBands("SL SR RL RR", hit, quiet));
            }
            return bands.ToArray();
        }
        // Drop and Well: 500 Hz is trimmed so a room does not box the step in, and 3.4 kHz (shoe grit) is raised
        // with the hit slider. The plugin adds the rest of the tick only while it is a hit.
        if (plan.Route == Route.Surround)
        {
            return
            [
                new("L R", Shelf.Low, 90, -(4f + quiet * 3f), 0.7f),
                new("L R", Shelf.None, 180, hit * 4f, 0.9f),
                new("L R", Shelf.None, 500, -2f, 1.0f),
                new("L R", Shelf.None, 1400, -(2f + quiet * 1.8f), 0.45f),
                new("L R", Shelf.None, 3400, hit * 3f, 1.4f),
                new("L R", Shelf.None, 7200, hit * 2.6f, 1.3f),
                new("SL SR RL RR", Shelf.None, 0, -10f, 1f),
                new("SL SR RL RR", Shelf.Low, 200, -(quiet * 8f), 0.7f),
            ];
        }
        if (plan.Preset == Preset.Well)
        {
            return
            [
                new("L R", Shelf.Low, 90, -(4f + quiet * 3f), 0.7f),
                new("L R", Shelf.None, 160, hit * 4.5f, 0.8f),
                new("L R", Shelf.None, 500, -1.5f, 1.0f),
                new("L R", Shelf.None, 1400, -(2f + quiet * 1.2f), 0.5f),
                new("L R", Shelf.None, 3400, hit * 2f, 1.4f),
                new("L R", Shelf.None, 7200, hit * 1.5f, 1.2f),
            ];
        }
        return
        [
            new("L R", Shelf.Low, 90, -(4f + quiet * 3f), 0.7f),
            new("L R", Shelf.None, 180, hit * 4f, 0.9f),
            new("L R", Shelf.None, 500, -2f, 1.0f),
            new("L R", Shelf.None, 1400, -(2f + quiet * 1.8f), 0.45f),
            new("L R", Shelf.None, 3400, hit * 3f, 1.4f),
            new("L R", Shelf.None, 7200, hit * 2.6f, 1.3f),
        ];
    }

    static Band[] NovaBands(string channels, float hit, float quiet) =>
    [
        new(channels, Shelf.Low, 90, -(4f + quiet * 3f), 0.7f),
        new(channels, Shelf.None, 220, hit * 4f, 1.0f),
        new(channels, Shelf.None, 1200, -(2.5f + quiet * 1.5f), 0.7f),
        new(channels, Shelf.None, 3200, hit * 6f, 1.1f),
        new(channels, Shelf.None, 8000, -4.5f, 1.6f),
    ];

    static float Lfe(MixPlan plan) => plan.Preset == Preset.Nova ? -24f : -(plan.Quiet / 100f * 18f);

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
