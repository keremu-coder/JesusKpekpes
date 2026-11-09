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
            var onFile = Path.Combine(folder, "on.txt");
            var plugin = OwnPlugin(folder);
            Replace(onFile, Mix.Config(plan, plugin));
            Replace(Path.Combine(folder, "switch.txt"), Mix.Switch(true, onFile));
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

    public static IReadOnlyList<string> Found()
    {
        var root = ConfigDir();
        return root is null ? [] : Mix.Conflicts(ReadOthers(root));
    }

    public static bool Clear()
    {
        var root = ConfigDir();
        if (root is null) return true;
        try
        {
            var folder = Path.Combine(root, FolderName);
            Directory.CreateDirectory(folder);
            Replace(Path.Combine(folder, "switch.txt"), Mix.Switch(false));
            return true;
        }
        catch (UnauthorizedAccessException) { return false; }
        catch (IOException) { return false; }
    }

    /// <summary>Deletes our folder and our one include line. Everything else in the config is left as it was.</summary>
    public static bool Remove()
    {
        var root = ConfigDir();
        if (root is null) return true;
        try
        {
            var folder = Path.Combine(root, FolderName);
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            var main = Path.Combine(root, "config.txt");
            if (!File.Exists(main)) return true;
            var lines = File.ReadAllLines(main);
            var kept = lines.Where(l => !l.Contains(FolderName, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (kept.Length != lines.Length)
                Replace(main, kept.Length == 0 ? "" : string.Join("\r\n", kept) + "\r\n");
            return true;
        }
        catch (UnauthorizedAccessException) { return false; }
        catch (IOException) { return false; }
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
        var body = current.TrimEnd();
        Replace(main, (body.Length == 0 ? "" : body + "\r\n") + Mix.IncludeLine + "\r\n");
    }

    static string? OwnPlugin(string folder)
    {
        var source = Path.Combine(AppContext.BaseDirectory, "JesusKpekpesTune.dll");
        if (!File.Exists(source)) return null;
        var dest = Path.Combine(folder, "JesusKpekpesTune.dll");
        try
        {
            var tmp = dest + ".jesuskpekpes_tmp";
            File.Copy(source, tmp, true);
            File.Move(tmp, dest, true);
            return dest;
        }
        catch (IOException)
        {
            return File.Exists(dest) ? dest : null;
        }
        catch (UnauthorizedAccessException)
        {
            return File.Exists(dest) ? dest : null;
        }
    }

    static void Replace(string path, string text)
    {
        var tmp = path + ".jesuskpekpes_tmp";
        try
        {
            File.WriteAllText(tmp, text);
            File.Move(tmp, path, true);
        }
        catch
        {
            try
            {
                if (File.Exists(tmp)) File.Delete(tmp);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }
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
