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

    /// <summary>Returns a sentence for the person. The setup is opened only when the hash matched.</summary>
    public static async Task<string> Fetch(string toolName)
    {
        string url, expected;
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("curl/8.0");
        try
        {
            if (toolName == "Equalizer APO")
            {
                var file = ApoFileFromRelease(await client.GetStringAsync(ApoRelease));
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
            await using (var net = await client.GetStreamAsync(url))
            await using (var disk = File.Create(path))
                await net.CopyToAsync(disk);

            if (!SameHash(expected, FileHash(path)))
            {
                try { File.Delete(path); } catch (IOException) { }
                return "The download is not the file we checked, so it was deleted and not opened. The vendor may have a newer version. Use Open page.";
            }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            return toolName == "Equalizer APO"
                ? "Checked and opened. In the setup, tick your playback device, finish, then restart Windows. Then press Check again."
                : "Checked and opened. Finish the setup, then press Check again.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return "The download did not finish: " + ex.Message + " Nothing was installed. Use Open page.";
        }
    }
}
