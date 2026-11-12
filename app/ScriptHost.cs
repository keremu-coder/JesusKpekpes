namespace JesusKpekpes;

static class ScriptHost
{
    public static string Script() => Path.Combine(AppContext.BaseDirectory, "pc", "Kpekpes.ps1");

    public static string Run(string args, int timeoutMs)
    {
        var script = Script();
        if (!File.Exists(script)) return "The PC script was not found next to the program. Reinstall or unzip the whole folder.";
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + script + "\" " + args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(script)!,
            };
            using var process = System.Diagnostics.Process.Start(start);
            if (process is null) return "Windows did not start PowerShell.";
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(true); } catch (Exception) { }
                return "That is taking too long, so it was stopped. Nothing new was measured.";
            }
            return (output.Result + "\r\n" + error.Result).Trim();
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}
