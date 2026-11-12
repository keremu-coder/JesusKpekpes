using System.Security.Cryptography;
using System.Text.Json;

namespace JesusKpekpes;

/// <summary>
/// Downloads a vendor's own setup file, checks it against a pinned SHA-256, and only then opens it.
/// The setup is not run silently: Equalizer APO needs the person to pick the playback device and restart.
/// A file that does not match is deleted and never opened.
/// </summary>
static class Installer
{
    // Hashes seen in the vendor probe run. A newer vendor release will not match, and is refused until the pin is updated.
    const string ApoSha256 = "7403BE7427BBE1936A40DDED082829B6E217FC4F5990FEE5CBA501F0AE055AFA";
    const string ReaPlugsSha256 = "1654F1F78D746FC87A6F6D078A8F928EA61C7B49997B1F2C07DFC870F8FA6C10";
    const string ApoRelease = "https://sourceforge.net/projects/equalizerapo/best_release.json";
    const string ReaPlugsUrl = "https://www.reaper.fm/reaplugs/reaplugs236_x64-install.exe";

    public static bool CanInstall(string toolName) => toolName is "Equalizer APO" or "ReaPlugs";

    public static bool SameHash(string expected, string actual) =>
        expected.Length == 64 && string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);

    public static string FileHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public static string? ApoFileFromRelease(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var name = doc.RootElement.GetProperty("platform_releases").GetProperty("windows").GetProperty("filename").GetString();
            return string.IsNullOrWhiteSpace(name) || !name.StartsWith('/') ? null : name;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Signer name when the file carries a valid Authenticode signature, otherwise null.</summary>
    public static string? SignedBy(string path)
    {
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -ExecutionPolicy Bypass -Command \"$s = Get-AuthenticodeSignature -LiteralPath $args[0]; if ($s.Status -eq 'Valid') { $s.SignerCertificate.Subject }\" \"" + path + "\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            using var process = System.Diagnostics.Process.Start(start);
            if (process is null) return null;
            var text = process.StandardOutput.ReadToEnd().Trim();
            if (!process.WaitForExit(30000)) { try { process.Kill(); } catch (InvalidOperationException) { } return null; }
            return text.Length == 0 ? null : text;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Returns a sentence for the person. The setup is opened only when it is the file we checked, carries a valid vendor signature,
    /// or the person was asked about an unchecked file and said yes (askUnchecked).
    /// </summary>
    public static async Task<string> Fetch(string toolName, IProgress<string>? progress = null, Func<string, bool>? askUnchecked = null)
    {
        string url, expected;
        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("curl/8.0");
        using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        try
        {
            if (toolName == "Equalizer APO")
            {
                progress?.Report("Finding the newest Equalizer APO...");
                var file = ApoFileFromRelease(await client.GetStringAsync(ApoRelease, limit.Token));
                if (file is null) return "SourceForge did not name a Windows file. Nothing was downloaded. Use Open page.";
                url = "https://sourceforge.net/projects/equalizerapo/files" + file + "/download";
                expected = ApoSha256;
            }
            else if (toolName == "ReaPlugs")
            {
                url = ReaPlugsUrl;
                expected = ReaPlugsSha256;
            }
            else return "No automatic download for " + toolName + ". Use Open page.";

            var folder = Path.Combine(Path.GetTempPath(), "jesus-kpekpes-setup", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, toolName.Replace(' ', '-') + "-setup.exe");
            using (var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, limit.Token))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? 0;
                await using var net = await response.Content.ReadAsStreamAsync(limit.Token);
                await using var disk = File.Create(path);
                var buffer = new byte[81920];
                long done = 0;
                int read;
                var lastPercent = -1;
                while ((read = await net.ReadAsync(buffer, limit.Token)) > 0)
                {
                    await disk.WriteAsync(buffer.AsMemory(0, read), limit.Token);
                    done += read;
                    var percent = total > 0 ? (int)(done * 100 / total) : (int)(done / 1_000_000);
                    if (percent == lastPercent) continue;
                    lastPercent = percent;
                    progress?.Report(total > 0
                        ? "Downloading " + toolName + "... " + percent + "%"
                        : "Downloading " + toolName + "... " + (done / 1_000_000) + " MB");
                }
            }

            progress?.Report("Checking " + toolName + "...");
            string how;
            if (SameHash(expected, FileHash(path))) how = "It matched the file we checked.";
            else
            {
                var signer = SignedBy(path);
                if (signer is not null) how = "The vendor has released a newer version. Its Windows signature is valid (" + signer + ").";
                else if (askUnchecked?.Invoke("The file for " + toolName + " is a newer version than the one we checked, and it has no valid vendor signature.\n\nOpen it anyway? Choose No if you are not sure.") == true)
                    how = "You chose to open a newer file that could not be checked.";
                else
                {
                    try { File.Delete(path); } catch (IOException) { }
                    return "The download is not the file we checked, so it was deleted and not opened. The vendor may have a newer version. Use Open page.";
                }
            }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            return toolName == "Equalizer APO"
                ? how + " Setup is open. Tick your playback device, finish, then restart Windows."
                : how + " Setup is open. Finish it.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return "The download did not finish: " + ex.Message + " Nothing was installed. Press the button to try again, or use Open page.";
        }
    }
}
