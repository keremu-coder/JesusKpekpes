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

    public static bool Installed(Tool tool) =>
        tool.Name == "Equalizer APO" ? ApoRoot() is not null : tool.Folders.Any(Directory.Exists);

    /// <summary>Where Equalizer APO is installed: the registry first (it can be on another drive), then Program Files.</summary>
    public static string? ApoRoot()
    {
        foreach (var view in new[] { Microsoft.Win32.RegistryView.Registry64, Microsoft.Win32.RegistryView.Registry32 })
        {
            try
            {
                using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, view);
                using var key = baseKey.OpenSubKey(@"SOFTWARE\EqualizerAPO");
                if (key?.GetValue("InstallPath") is string path && Directory.Exists(Path.Combine(path, "config"))) return path;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        }
        var normal = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "EqualizerAPO");
        return Directory.Exists(Path.Combine(normal, "config")) ? normal : null;
    }

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
    public static string Verdict(long idleMs, long loadMs, bool sampleFinished)
    {
        if (idleMs < 0) return "No answer from the internet at all. Check that you are online, then try again.";
        var text = new System.Text.StringBuilder();
        text.Append("Delay to the internet when nothing else is happening: ").Append(idleMs).AppendLine(" ms.");
        text.AppendLine(idleMs switch
        {
            < 40 => "That is very good for online games.",
            < 80 => "That is fine for online games.",
            < 150 => "That is high. You will feel it in online games. A network cable instead of Wi-Fi usually helps most.",
            _ => "That is too high for smooth online play. This is your internet line or Wi-Fi, not your PC, so no PC setting can fix it. Try a cable, sit closer to the router, or ask your internet provider.",
        });
        if (loadMs >= 0)
            text.Append("Delay while a download was running: ").Append(loadMs).AppendLine(" ms.")
                .AppendLine(loadMs > idleMs + 60
                    ? "It got worse while data was moving. If someone streams or downloads while you play, you will lag."
                    : "It did not get worse while data was moving. Good.");
        if (!sampleFinished)
            text.AppendLine("A small test download did not finish in time, so your connection is slow. That is another sign the line is the limit.");
        text.Append("This is a short check, not a lab test.");
        return text.ToString();
    }

    public static async Task<string> Run()
    {
        var idle = await Times();
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        var download = client.GetByteArrayAsync("https://speed.cloudflare.com/__down?bytes=4000000");
        var loaded = await Times();
        var finished = true;
        try { await download; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { finished = false; }
        return Verdict(Median(idle), Median(loaded), finished).Replace("\r\n", "\n").Replace("\n", "\r\n");
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
            catch (System.Net.NetworkInformation.PingException) { }
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
