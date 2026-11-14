using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace JesusKpekpes;

/// <summary>
/// Updates the program from the newest GitHub release, from inside the program.
/// It never runs on its own schedule and never blocks the window: a failed check says nothing and changes nothing.
/// The new zip is checked against the release's SHA256SUMS.txt, unpacked into a folder only an administrator can write,
/// and started once with --selftest. Only then is the old window closed and the files swapped. Every replaced file is
/// kept as .kpekpes-old until the swap is complete, so a failure puts the old version back exactly as it was.
/// Nothing outside the program folder is touched except the version number in Windows Settings.
/// </summary>
static class Updater
{
    public const string Repo = "keremu-coder/JesusKpekpes";
    public const string ZipName = "JesusKpekpes-win-x64.zip";
    public const string SumsName = "SHA256SUMS.txt";
    public const string OldSuffix = ".kpekpes-old";
    public const string WorkFolder = ".update";
    const string DownloadPrefix = "https://github.com/" + Repo + "/releases/download/";
    const long MaxZip = 500L * 1024 * 1024;
    const long MaxUnpacked = 1500L * 1024 * 1024;

    public sealed record Found(Version Version, string Tag, string ZipUrl, string SumsUrl);

    public sealed record Staged(bool Ok, string Message, string Exe, string Folder);

    static readonly string[] Required = ["JesusKpekpes.exe", "JesusKpekpesTune.dll", "pc/Kpekpes.ps1"];

    public static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    public static Version Current() => Normalize(typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0, 0));

    /// <summary>A build made by hand or by the "build" workflow carries 0.0.0 and must never replace itself.</summary>
    public static bool CanUpdate(Version current) => current > new Version(0, 0, 0);

    /// <summary>Only plain x.y.z, with an optional leading v. Anything else (build-abc123, 1.0.9-beta) is not a release.</summary>
    public static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = Regex.Match(text.Trim(), @"^v?(\d{1,4})\.(\d{1,4})\.(\d{1,4})$");
        if (!m.Success) return null;
        return new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value));
    }

    /// <summary>
    /// The newest published release that is newer than the running version and carries both files.
    /// Drafts, pre-releases and releases without the zip or the checksum file (a failed build) are passed over.
    /// </summary>
    public static Found? Pick(string json, Version current)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
            var floor = Normalize(current);
            Found? best = null;
            foreach (var release in doc.RootElement.EnumerateArray())
            {
                if (release.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) continue;
                if (release.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True) continue;
                if (!release.TryGetProperty("tag_name", out var tagValue) || tagValue.ValueKind != JsonValueKind.String) continue;
                var tag = tagValue.GetString();
                var version = ParseVersion(tag);
                if (version is null || version <= floor) continue;
                if (best is not null && version <= best.Version) continue;
                if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) continue;
                string? zip = null, sums = null;
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                    var url = asset.TryGetProperty("browser_download_url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
                    if (name is null || url is null || !url.StartsWith(DownloadPrefix, StringComparison.Ordinal)) continue;
                    if (name == ZipName) zip = url;
                    else if (name == SumsName) sums = url;
                }
                if (zip is null || sums is null) continue;
                best = new Found(version, tag!, zip, sums);
            }
            return best;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>The SHA-256 written next to a file name in SHA256SUMS.txt, or null.</summary>
    public static string? HashFor(string sums, string file)
    {
        foreach (var raw in sums.Split('\n'))
        {
            var parts = raw.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2) continue;
            var name = parts[1].Trim().TrimStart('*');
            if (!string.Equals(name, file, StringComparison.OrdinalIgnoreCase)) continue;
            if (Regex.IsMatch(parts[0], "^[0-9A-Fa-f]{64}$")) return parts[0].ToUpperInvariant();
        }
        return null;
    }

    /// <summary>Where an entry would land. Throws when it would land outside the folder.</summary>
    public static string SafeDest(string root, string entryName)
    {
        var name = entryName.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(root, name));
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The update has an unsafe path: " + entryName);
        return full;
    }

    public static void Unpack(string zipPath, string dest)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;
            SafeDest(dest, entry.FullName);
            total += entry.Length;
            if (entry.Length > MaxZip || total > MaxUnpacked) throw new InvalidDataException("The update is larger than expected.");
        }
        Directory.CreateDirectory(dest);
        foreach (var entry in zip.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;
            var target = SafeDest(dest, entry.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, true);
        }
    }

    static bool SameBytes(string a, string b)
    {
        using var left = File.OpenRead(a);
        using var right = File.OpenRead(b);
        if (left.Length != right.Length) return false;
        var x = new byte[81920];
        var y = new byte[81920];
        while (true)
        {
            var n = left.Read(x, 0, x.Length);
            if (n == 0) return true;
            var read = 0;
            while (read < n)
            {
                var got = right.Read(y, read, n - read);
                if (got == 0) return false;
                read += got;
            }
            if (!x.AsSpan(0, n).SequenceEqual(y.AsSpan(0, n))) return false;
        }
    }

    /// <summary>
    /// Lays the new files over the program folder. Files that are the same are left alone. A file that is replaced is first
    /// renamed to name.kpekpes-old. Files the update does not contain are never deleted. If anything fails, every step is
    /// undone and the reason is returned. An empty string means it worked. failOn is only for tests.
    /// </summary>
    public static string Apply(string stage, string install, Func<string, bool>? failOn = null)
    {
        var moved = new List<(string Old, string Dest)>();
        var made = new List<string>();
        try
        {
            var root = Path.GetFullPath(stage).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            foreach (var src in Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetFullPath(src).Substring(root.Length);
                var dest = SafeDest(install, rel);
                if (failOn?.Invoke(rel) == true) throw new IOException("stopped on purpose at " + rel);
                if (File.Exists(dest))
                {
                    if (SameBytes(src, dest)) continue;
                    var old = dest + OldSuffix;
                    File.Move(dest, old, true);
                    moved.Add((old, dest));
                }
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                made.Add(dest);
                File.Copy(src, dest, false);
            }
            return "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            for (var i = made.Count - 1; i >= 0; i--)
            {
                try { File.Delete(made[i]); } catch (Exception) { /* the next step still puts the old file back */ }
            }
            for (var i = moved.Count - 1; i >= 0; i--)
            {
                try { File.Move(moved[i].Old, moved[i].Dest, true); } catch (Exception) { /* nothing more can be done here */ }
            }
            return ex.Message;
        }
    }

    /// <summary>Removes the kept old files and the work folder. Best effort; a locked file is tried again at the next start.</summary>
    public static void Tidy(string install)
    {
        try
        {
            foreach (var old in Directory.EnumerateFiles(install, "*" + OldSuffix, SearchOption.AllDirectories))
            {
                try { File.Delete(old); } catch (Exception) { /* later */ }
            }
            var work = Path.Combine(install, WorkFolder);
            if (Directory.Exists(work)) Directory.Delete(work, true);
        }
        catch (Exception) { /* later */ }
    }

    public static string ResultFile() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "JesusKpekpes", "update-result.txt");

    public static string Describe(string line)
    {
        var bar = line.IndexOf('|');
        var kind = bar < 0 ? line : line[..bar];
        var rest = bar < 0 ? "" : line[(bar + 1)..].Trim();
        return kind == "ok"
            ? "Updated to version " + rest + ". If the sound changed, open Audio tune and press Turn on once."
            : "The update did not install: " + rest + " You are still on the old version.";
    }

    /// <summary>What the last update did, once. Null when there was no update since the last start.</summary>
    public static string? TakeResult()
    {
        try
        {
            var path = ResultFile();
            if (!File.Exists(path)) return null;
            var line = File.ReadAllText(path).Trim();
            File.Delete(path);
            return line.Length == 0 ? null : Describe(line);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public static async Task<Found?> Check(Version current)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("JesusKpekpes-updater");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            var json = await client.GetStringAsync("https://api.github.com/repos/" + Repo + "/releases?per_page=15");
            return Pick(json, current);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            return null;
        }
    }

    /// <summary>Downloads, checks and unpacks the release, and proves the new program starts. Changes nothing outside the work folder.</summary>
    public static async Task<Staged> Prepare(Found found, string install, IProgress<string>? progress = null)
    {
        var work = Path.Combine(install, WorkFolder);
        var failed = new Func<string, Staged>(message =>
        {
            try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch (Exception) { /* later */ }
            return new Staged(false, message, "", "");
        });
        try
        {
            if (Directory.Exists(work)) Directory.Delete(work, true);
            Directory.CreateDirectory(work);
            using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("JesusKpekpes-updater");
            using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(10));

            progress?.Report("Checking the new version...");
            var sums = await client.GetStringAsync(found.SumsUrl, limit.Token);
            var expected = HashFor(sums, ZipName);
            if (expected is null) return failed("The release has no checksum for the program.");

            var zipPath = Path.Combine(work, "update.zip");
            using (var response = await client.GetAsync(found.ZipUrl, HttpCompletionOption.ResponseHeadersRead, limit.Token))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? 0;
                if (total > MaxZip) return failed("The download is larger than expected.");
                await using var net = await response.Content.ReadAsStreamAsync(limit.Token);
                await using var disk = File.Create(zipPath);
                var buffer = new byte[81920];
                long done = 0;
                var last = -1;
                int read;
                while ((read = await net.ReadAsync(buffer, limit.Token)) > 0)
                {
                    done += read;
                    if (done > MaxZip) return failed("The download is larger than expected.");
                    await disk.WriteAsync(buffer.AsMemory(0, read), limit.Token);
                    var percent = total > 0 ? (int)(done * 100 / total) : (int)(done / 1_000_000);
                    if (percent == last) continue;
                    last = percent;
                    progress?.Report(total > 0 ? "Downloading the update... " + percent + "%" : "Downloading the update... " + (done / 1_000_000) + " MB");
                }
            }

            progress?.Report("Checking the download...");
            var actual = await Task.Run(() => Installer.FileHash(zipPath));
            if (!Installer.SameHash(expected, actual)) return failed("The download did not match its checksum, so it was thrown away.");

            var folder = Path.Combine(work, "new");
            await Task.Run(() => Unpack(zipPath, folder));
            try { File.Delete(zipPath); } catch (Exception) { /* later */ }
            foreach (var need in Required)
            {
                if (!File.Exists(SafeDest(folder, need))) return failed("The update is missing " + need + ".");
            }

            var exe = Path.Combine(folder, "JesusKpekpes.exe");
            var info = FileVersionInfo.GetVersionInfo(exe);
            var inside = new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart);
            if (inside != found.Version) return failed("The program inside is version " + inside.ToString(3) + ", not " + found.Version.ToString(3) + ".");

            var signer = await Task.Run(() =>
            {
                var mine = Environment.ProcessPath is { } self ? Installer.SignedBy(self) : null;
                if (mine is null) return "";
                return string.Equals(mine, Installer.SignedBy(exe), StringComparison.Ordinal) ? "" : "The new program is not signed by the same publisher.";
            });
            if (signer.Length > 0) return failed(signer);

            progress?.Report("Trying the new version...");
            var trouble = await Task.Run(() => Trial(exe, folder));
            if (trouble is not null) return failed(trouble);
            return new Staged(true, "", exe, folder);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or UnauthorizedAccessException or InvalidDataException or Win32Exception)
        {
            return failed("The download did not finish: " + ex.Message);
        }
    }

    /// <summary>Starts the new program once with --selftest. Null when it started and ended cleanly, otherwise a sentence.</summary>
    static string? Trial(string exe, string folder)
    {
        var start = new ProcessStartInfo(exe, "--selftest") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = folder };
        using var trial = Process.Start(start);
        if (trial is null) return "The new version could not be started.";
        if (!trial.WaitForExit(30000))
        {
            try { trial.Kill(true); } catch (Exception) { /* gone */ }
            return "The new version did not start in time.";
        }
        return trial.ExitCode == 0 ? null : "The new version did not start.";
    }

    static string Quote(string path) => "\"" + path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + "\"";

    /// <summary>Starts the new program's own finisher. The caller must close its window right after this returns true.</summary>
    public static bool StartFinish(Staged staged, Version version, string install)
    {
        var args = "--finish-update " + Environment.ProcessId + " " + Quote(install) + " " + Quote(staged.Folder) + " " + version.ToString(3) + " " + Quote(ResultFile());
        try
        {
            Process.Start(new ProcessStartInfo(staged.Exe, args) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = staged.Folder });
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    static void Report(string file, string line)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, line);
        }
        catch (Exception) { /* the window opens either way */ }
    }

    static void SetRegisteredVersion(string install, string version)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\JesusKpekpes", true);
            if (key?.GetValue("InstallLocation") is not string where) return;
            if (!string.Equals(where.TrimEnd('\\'), install.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return;
            key.SetValue("DisplayVersion", version);
        }
        catch (Exception) { /* Windows Settings keeps the old number until the next setup */ }
    }

    /// <summary>
    /// Runs inside the NEW program, from the work folder. Waits for the old window to end, swaps the files, records the
    /// result, and opens the installed program. If the old window does not end, nothing is changed and nothing is opened.
    /// </summary>
    public static void Finish(int pid, string install, string stage, string version, string resultFile)
    {
        try
        {
            try
            {
                using var old = Process.GetProcessById(pid);
                if (!old.WaitForExit(60000))
                {
                    Report(resultFile, "failed|The old window did not close in time.");
                    return;
                }
            }
            catch (ArgumentException)
            {
                /* already gone */
            }
            var problem = Apply(stage, install);
            if (problem.Length == 0)
            {
                SetRegisteredVersion(install, version);
                Report(resultFile, "ok|" + version);
            }
            else Report(resultFile, "failed|" + problem);
        }
        catch (Exception ex)
        {
            Report(resultFile, "failed|" + ex.Message);
        }
        try
        {
            var exe = Path.Combine(install, "JesusKpekpes.exe");
            if (File.Exists(exe)) Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = install });
        }
        catch (Exception) { /* the person can open it from the Start menu */ }
    }
}
