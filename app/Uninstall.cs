using Microsoft.Win32;

namespace JesusKpekpes;

static class Uninstall
{
    public static void Run()
    {
        var dir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var installed = dir.Equals(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Jesus Kpekpes"), StringComparison.OrdinalIgnoreCase);
        var ask = installed
            ? "Remove Jesus Kpekpes and put the PC changes back? Audio is left as it is."
            : "This copy was not installed by the setup. Put the PC changes back? This folder is not deleted.";
        if (MessageBox.Show(ask, "Uninstall", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
        var script = Path.Combine(dir, "pc", "Kpekpes.ps1");
        if (File.Exists(script)) ScriptHost.Run("-Action Undo -Module Both -Silent", 180000);
        if (!installed)
        {
            MessageBox.Show("The PC changes were put back if a journal was there. Delete this folder yourself. Audio was not touched.", "Uninstall");
            return;
        }
        try
        {
            var link = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "Jesus Kpekpes.lnk");
            if (File.Exists(link)) File.Delete(link);
        }
        catch (Exception) { }
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", true);
            key?.DeleteSubKeyTree("JesusKpekpes", false);
        }
        catch (Exception) { }
        var bat = Path.Combine(Path.GetTempPath(), "kpekpes-uninstall.bat");
        File.WriteAllText(bat, "@echo off\r\ntimeout /t 2 /nobreak >nul\r\nrd /s /q \"" + dir + "\"\r\n");
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c \"" + bat + "\"") { CreateNoWindow = true, UseShellExecute = false });
    }
}
