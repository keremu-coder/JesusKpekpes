namespace JesusKpekpes;

static class AudioKit
{
    public sealed record Tool(string Name, string Why, string Url, string[] Folders);

    public static readonly Tool[] Tools =
    [
        new("Equalizer APO", "Required. Our tune is written here, and nowhere else.", "https://equalizerapo.com/",
            [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "EqualizerAPO")]),
        new("HeSuVi", "Optional. Rara uses this too. We do not turn it on.", "https://sourceforge.net/projects/hesuvi/",
            [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "EqualizerAPO", "config", "HeSuVi")]),
        new("ReaPlugs", "Optional. Same family of tools. We do not load them.", "https://www.reaper.fm/reaplugs/",
            [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "VSTPlugins", "ReaPlugs"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "REAPER", "Plugins"),
            ]),
        new("VoiceMeeter", "Optional. We do not route the game through it.", "https://vb-audio.com/Voicemeeter/",
            [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "VB", "Voicemeeter"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "VB", "Voicemeeter"),
            ]),
    ];

    public static bool Installed(Tool tool) => tool.Folders.Any(Directory.Exists);

    public static void Open(Tool tool) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(tool.Url) { UseShellExecute = true });
}

static class CodFind
{
    public static string Report()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Call of Duty", "players");
        if (!Directory.Exists(dir)) return "No Call of Duty config folder yet.\r\n" + dir + "\r\nNothing was written.";
        var files = Directory.GetFiles(dir, "options*.cst");
        if (files.Length == 0) return "The folder is there, but there is no options file.\r\n" + dir + "\r\nNothing was written.";
        var text = new System.Text.StringBuilder();
        text.AppendLine("Found. Nothing was written.");
        foreach (var file in files)
        {
            text.AppendLine(file);
            foreach (var line in File.ReadLines(file))
            {
                if (line.Contains("AudioWantedChannelsNumber", StringComparison.OrdinalIgnoreCase))
                    text.AppendLine("  " + line.Trim());
            }
        }
        text.AppendLine("The game file is left alone. Stereo is the audio default, so 2 channels matches it.");
        return text.ToString();
    }
}

static class NetCheck
{
    public static async Task<string> Run()
    {
        var idle = await Times();
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        var download = client.GetByteArrayAsync("https://speed.cloudflare.com/__down?bytes=8000000");
        var loaded = await Times();
        var note = "";
        try { await download; }
        catch (Exception ex) { note = "\r\nThe load sample did not finish: " + ex.Message; }
        var idleMs = Median(idle);
        var loadMs = Median(loaded);
        var verdict = loadMs > idleMs + 40
            ? "The line got slower while data was moving."
            : "The line stayed close while data was moving.";
        return "Idle " + idleMs + " ms. Under load " + loadMs + " ms.\r\n" + verdict + "\r\nThis is a short check, not a lab test." + note;
    }

    static async Task<List<long>> Times()
    {
        var times = new List<long>();
        using var ping = new System.Net.NetworkInformation.Ping();
        for (var i = 0; i < 6; i++)
        {
            try
            {
                var reply = await ping.SendPingAsync("1.1.1.1", 1000);
                if (reply.Status == System.Net.NetworkInformation.IPStatus.Success) times.Add(reply.RoundtripTime);
            }
            catch { }
        }
        return times;
    }

    static long Median(List<long> times)
    {
        if (times.Count == 0) return -1;
        times.Sort();
        return times[times.Count / 2];
    }
}
