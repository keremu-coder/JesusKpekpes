using System.Text;
using System.Text.RegularExpressions;

namespace JesusKpekpes;

/// <summary>Turns the PC script's check lines into a short answer a person without PC knowledge can act on. No windows in here.</summary>
static class PcReport
{
    public sealed record Item(string State, string Name, string Detail);

    static readonly Regex ItemLine = new(@"^\s*\[(PASS|FAIL|WARN|NA|INFO)\]\s+(.*)$", RegexOptions.Compiled);
    static readonly Regex ScoreLine = new(@"SCORE:\s*(\d+)%\s*\((\d+) of (\d+) checks passed, (\d+) warnings", RegexOptions.Compiled);

    public static string Normalize(string raw) =>
        raw.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n");

    public static List<Item> Items(string raw)
    {
        var items = new List<Item>();
        foreach (var line in raw.Replace("\r\n", "\n").Split('\n'))
        {
            var m = ItemLine.Match(line);
            if (!m.Success) continue;
            var rest = m.Groups[2].Value.Trim();
            var cut = rest.IndexOf("  - ", StringComparison.Ordinal);
            items.Add(cut < 0 ? new Item(m.Groups[1].Value, rest, "") : new Item(m.Groups[1].Value, rest[..cut].Trim(), rest[(cut + 4)..].Trim()));
        }
        return items;
    }

    public static string Group(string name)
    {
        bool Has(params string[] words) => words.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase));
        if (Has("Xbox", "Removed:")) return "Xbox apps that run in the background";
        if (Has("NVIDIA", "ShadowPlay")) return "NVIDIA graphics card";
        if (Has("Delivery Optimization")) return "Windows background activity";
        if (Has("DS4", "Controller", "Profile", "Installed:", "optimizer", "Logon", "Undo information", "Script folder", "Timer task")) return "Controller and undo safety";
        if (Has("priority")) return "Game gets first call on the CPU and disk";
        if (Has("Tcp", "TCP", "Nagle", "QoS", "adapter", "congestion", "Wi-Fi", "Ethernet")) return "Internet delay";
        if (Has("Game", "DVR", "HDR", "fullscreen", "FSE", "DXGI", "MPO", "GPU scheduling", "Capture")) return "Game settings";
        if (Has("Service", "Cortana", "Copilot", "Suggested", "Background", "Search box", "Sync provider", "driver updates", "auto-reboot", "Store")) return "Windows background activity";
        if (Has("Visual", "Menu delay", "animation", "Transparency", "Mouse", "Sticky", "Filter Keys", "Toggle Keys", "MMCSS", "responsiveness")) return "Smoothness and mouse feel";
        return "Power and speed";
    }

    /// <summary>The answer for the Check this PC button.</summary>
    public static string Verify(string raw, string? extra = null)
    {
        var items = Items(raw);
        var text = new StringBuilder();
        var scores = ScoreLine.Matches(raw).Select(m => (Percent: m.Groups[1].Value, Pass: m.Groups[2].Value, Total: m.Groups[3].Value)).ToList();
        if (scores.Count == 0)
        {
            text.AppendLine("The check did not give a score. The report is below.");
        }
        else
        {
            text.Append("Your PC is ").Append(scores[0].Percent).Append("% ready for gaming (").Append(scores[0].Pass).Append(" of ").Append(scores[0].Total).AppendLine(" settings are right).");
            if (scores.Count > 1) text.Append("Your controller is ").Append(scores[1].Percent).AppendLine("% ready.");
            text.AppendLine("This check only looked. Nothing was changed.");
            text.AppendLine("To fix everything below in one go, press Optimize PC and controller. Undo that puts it all back.");
        }
        var fails = items.Where(i => i.State == "FAIL").ToList();
        if (fails.Count > 0)
        {
            text.AppendLine().AppendLine("NEEDS FIXING (" + fails.Count + " settings), grouped:");
            foreach (var group in fails.GroupBy(i => Group(i.Name)).OrderByDescending(g => g.Count()))
                text.AppendLine("  - " + group.Key + ": " + group.Count());
        }
        var warns = items.Where(i => i.State == "WARN").ToList();
        if (warns.Count > 0)
        {
            text.AppendLine().AppendLine("WORTH DOING YOURSELF (Optimize cannot do these):");
            foreach (var w in warns) text.AppendLine("  - " + w.Name + (w.Detail.Length > 0 ? ": " + w.Detail : ""));
        }
        var good = items.Count(i => i.State == "PASS");
        if (good > 0) text.AppendLine().AppendLine("Already good: " + good + " settings.");
        var gpu = items.FirstOrDefault(i => i.State == "INFO" && i.Name.StartsWith("GPU", StringComparison.OrdinalIgnoreCase));
        if (gpu is not null) text.AppendLine().AppendLine(gpu.Name + (gpu.Detail.Length > 0 ? ": " + gpu.Detail : ""));
        if (!string.IsNullOrWhiteSpace(extra)) text.AppendLine().AppendLine(extra.Trim());
        text.AppendLine().AppendLine("---- Full list, if you want it ----");
        foreach (var i in items.Where(i => i.State != "PASS"))
            text.AppendLine("[" + i.State + "] " + i.Name + (i.Detail.Length > 0 ? " - " + i.Detail : ""));
        return Normalize(text.ToString());
    }

    /// <summary>One plain sentence for the state of a finished Optimize run.</summary>
    public static string Optimized(string raw, int exitCode)
    {
        var restart = raw.Contains("Restart Windows", StringComparison.OrdinalIgnoreCase);
        var problems = raw.Contains("did not apply", StringComparison.OrdinalIgnoreCase) || raw.Contains("stopped:", StringComparison.OrdinalIgnoreCase);
        var head = exitCode != 0 ? "It stopped before it finished. Read the lines below."
            : problems ? "Finished, but a few things did not apply. They are listed below."
            : "Finished. Your PC and controller settings are set.";
        return head + (restart && exitCode == 0 ? " Restart Windows once so everything takes effect, then press Check this PC to see the new score." : "")
            + "\r\n\r\n---- What it did ----\r\n" + Normalize(raw.Trim());
    }
}
