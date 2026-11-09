namespace JesusKpekpes;

static class InPlace
{
    const string FileName = "jesus-kpekpes.txt";
    const string IncludeLine = "Include: jesus-kpekpes.txt";

    public static bool Apply(string headphoneName, int hitAmount, int quiet)
    {
        var dir = ConfigDir();
        if (dir is null) return false;
        var room = quiet / 100f * 8f;
        var hit = hitAmount / 100f * 6f;
        var peak = room + hit;
        var body = room + hit * 0.45f;
        var center = -(quiet / 100f * 10f);
        var gun = -(quiet / 100f * 6f);
        var lfe = -(quiet / 100f * 24f);
        var text =
            "Device: " + headphoneName + "\r\n" +
            "Channel: C\r\n" +
            "Preamp: " + center.ToString("0.0") + " dB\r\n" +
            "Channel: L R\r\n" +
            "Filter: ON LSC Fc 140 Hz Gain " + gun.ToString("0.0") + " dB Q 0.7\r\n" +
            "Channel: SL SR RL RR\r\n" +
            "Preamp: " + (-room).ToString("0.0") + " dB\r\n" +
            "Filter: ON PK Fc 280 Hz Gain " + body.ToString("0.0") + " dB Q 0.8\r\n" +
            "Filter: ON PK Fc 3400 Hz Gain " + peak.ToString("0.0") + " dB Q 1.2\r\n" +
            "Channel: LFE\r\n" +
            "Preamp: " + lfe.ToString("0.0") + " dB\r\n";
        try
        {
            File.WriteAllText(Path.Combine(dir, FileName), text);
            var main = Path.Combine(dir, "config.txt");
            if (!File.Exists(main))
                File.WriteAllText(main, IncludeLine + "\r\n");
            else if (!File.ReadAllText(main).Contains(FileName, StringComparison.OrdinalIgnoreCase))
                File.AppendAllText(main, "\r\n" + IncludeLine + "\r\n");
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static void Clear()
    {
        var dir = ConfigDir();
        if (dir is null) return;
        var own = Path.Combine(dir, FileName);
        if (File.Exists(own)) File.Delete(own);
        var main = Path.Combine(dir, "config.txt");
        if (!File.Exists(main)) return;
        var lines = File.ReadAllLines(main).Where(line => !line.Contains(FileName, StringComparison.OrdinalIgnoreCase));
        File.WriteAllLines(main, lines);
    }

    static string? ConfigDir()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "EqualizerAPO", "config");
        return Directory.Exists(dir) ? dir : null;
    }
}
