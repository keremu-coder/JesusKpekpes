using Microsoft.Win32;

namespace JesusKpekpes;

enum WriteResult { Missing, Denied, Conflict, NotAttached, On }

static class InPlace
{
    const string FolderName = "jesus-kpekpes";

    public static WriteResult Apply(string deviceId, MixPlan plan, bool keepOthers)
    {
        var root = ConfigDir();
        if (root is null) return WriteResult.Missing;
        var others = ReadOthers(root);
        if (!keepOthers && Mix.Conflicts(others).Count > 0) return WriteResult.Conflict;
        try
        {
            var folder = Path.Combine(root, FolderName);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "on.txt"), Mix.Config(plan));
            File.WriteAllText(Path.Combine(folder, "switch.txt"), Mix.Switch(true));
            EnsureInclude(root);
            return Attached(deviceId) ? WriteResult.On : WriteResult.NotAttached;
        }
        catch (UnauthorizedAccessException)
        {
            return WriteResult.Denied;
        }
        catch (IOException)
        {
            return WriteResult.Denied;
        }
    }

    public static void Clear()
    {
        var root = ConfigDir();
        if (root is null) return;
        var folder = Path.Combine(root, FolderName);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "switch.txt"), Mix.Switch(false));
    }

    public static bool Attached(string deviceId)
    {
        var guid = deviceId;
        var dot = deviceId.LastIndexOf('.');
        if (dot >= 0 && dot < deviceId.Length - 1) guid = deviceId[(dot + 1)..];
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\EqualizerAPO\Child APOs\" + guid);
        if (key is null) return false;
        var pre = key.GetValue("PreMixChild") as string;
        var post = key.GetValue("PostMixChild") as string;
        return !string.IsNullOrWhiteSpace(pre) || !string.IsNullOrWhiteSpace(post);
    }

    static void EnsureInclude(string root)
    {
        var main = Path.Combine(root, "config.txt");
        var current = File.Exists(main) ? File.ReadAllText(main) : "";
        if (current.Contains(FolderName, StringComparison.OrdinalIgnoreCase)) return;
        var backup = main + ".jesus-kpekpes.bak";
        if (File.Exists(main) && !File.Exists(backup)) File.Copy(main, backup);
        File.AppendAllText(main, (current.Length == 0 ? "" : "\r\n") + Mix.IncludeLine + "\r\n");
    }

    static string ReadOthers(string root)
    {
        var text = "";
        var main = Path.Combine(root, "config.txt");
        if (File.Exists(main)) text += File.ReadAllText(main);
        foreach (var file in Directory.EnumerateFiles(root, "*.txt"))
        {
            if (Path.GetFileName(file).Contains(FolderName, StringComparison.OrdinalIgnoreCase)) continue;
            text += "\n" + File.ReadAllText(file);
        }
        return text;
    }

    static string? ConfigDir()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "EqualizerAPO", "config");
        return Directory.Exists(dir) ? dir : null;
    }
}
