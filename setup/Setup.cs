using System.IO.Compression;
using System.Reflection;
using Microsoft.Win32;

namespace JesusKpekpes.Setup;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new SetupForm());
    }
}

sealed class SetupForm : Form
{
    readonly Label _status = new();

    public SetupForm()
    {
        Text = "Install Jesus Kpekpes";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(520, 280);
        BackColor = Color.FromArgb(7, 9, 15);
        ForeColor = Color.FromArgb(242, 243, 245);
        Font = new Font("Segoe UI", 10f);
        var body = new Label
        {
            Text = "Installs into Program Files, adds a Start menu shortcut, and adds an entry in Windows Settings so you can uninstall it. Uninstall puts the PC changes back. Audio is left as it is.",
            Bounds = new Rectangle(20, 16, 480, 72),
            ForeColor = Color.FromArgb(180, 184, 198),
        };
        _status.SetBounds(20, 100, 480, 80);
        var install = new Button
        {
            Text = "Install",
            Bounds = new Rectangle(20, 200, 160, 44),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(88, 101, 242),
            ForeColor = Color.White,
        };
        install.FlatAppearance.BorderSize = 0;
        install.Click += (_, _) => Install(install);
        var had = Existing();
        if (had is { } found)
        {
            var mine = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
            var same = string.Equals(found.Version, mine, StringComparison.Ordinal);
            _status.Text = same
                ? "Jesus Kpekpes " + found.Version + " is already installed. You do not need to install it again. Open it, or press Reinstall to repair it."
                : "Jesus Kpekpes " + found.Version + " is installed. This setup has " + mine + ". Press Update to replace it.";
            install.Text = same ? "Reinstall" : "Update";
            var open = new Button
            {
                Text = "Open Jesus Kpekpes",
                Bounds = new Rectangle(200, 200, 220, 44),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(40, 120, 80),
                ForeColor = Color.White,
            };
            open.FlatAppearance.BorderSize = 0;
            open.Click += (_, _) =>
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(found.Exe) { UseShellExecute = true });
                Close();
            };
            Controls.Add(open);
        }
        Controls.Add(body);
        Controls.Add(_status);
        Controls.Add(install);
    }

    /// <summary>The installed copy, if the Windows uninstall entry is there and its program file still exists.</summary>
    static (string Version, string Exe)? Existing()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\JesusKpekpes");
            if (key?.GetValue("InstallLocation") is not string dir) return null;
            var exe = Path.Combine(dir, "JesusKpekpes.exe");
            if (!File.Exists(exe)) return null;
            return (key.GetValue("DisplayVersion") as string ?? "?", exe);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }

    void Install(Button button)
    {
        button.Enabled = false;
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Jesus Kpekpes");
        try
        {
            using var zip = OpenPayload();
            if (zip is null)
            {
                _status.Text = "The installer does not contain the program. Nothing was installed.";
                button.Enabled = true;
                return;
            }
            if (System.Diagnostics.Process.GetProcessesByName("JesusKpekpes").Length > 0)
            {
                _status.Text = "Close Jesus Kpekpes first. If you cannot see it, right-click the logo by the clock and choose Exit. Nothing was changed.";
                button.Enabled = true;
                return;
            }
            Directory.CreateDirectory(dir);
            var root = Path.GetFullPath(dir + Path.DirectorySeparatorChar);
            foreach (var entry in zip.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;
                var dest = Path.GetFullPath(Path.Combine(dir, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException(entry.FullName);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                entry.ExtractToFile(dest, true);
            }
            var exe = Path.Combine(dir, "JesusKpekpes.exe");
            if (!File.Exists(exe)) throw new FileNotFoundException("JesusKpekpes.exe was not in the installer.");
            WriteShortcut(exe, dir);
            WriteUninstall(exe, dir);
            _status.Text = "Installed. Opening it now. This window will close.";
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true });
            Close();
        }
        catch (Exception ex)
        {
            _status.Text = "Install stopped: " + ex.Message;
            button.Enabled = true;
        }
    }

    static ZipArchive? OpenPayload()
    {
        var embedded = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip");
        if (embedded is not null) return new ZipArchive(embedded, ZipArchiveMode.Read);
        var beside = Path.Combine(AppContext.BaseDirectory, "payload.zip");
        if (!File.Exists(beside)) return null;
        return new ZipArchive(File.OpenRead(beside), ZipArchiveMode.Read);
    }

    static void WriteShortcut(string exe, string dir)
    {
        var link = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "Jesus Kpekpes.lnk");
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Windows did not provide a way to make the shortcut.");
        dynamic shell = Activator.CreateInstance(shellType) ?? throw new InvalidOperationException("Windows did not make the shortcut.");
        dynamic shortcut = shell.CreateShortcut(link);
        shortcut.TargetPath = exe;
        shortcut.WorkingDirectory = dir;
        shortcut.Description = "Jesus Kpekpes";
        shortcut.Save();
    }

    static void WriteUninstall(string exe, string dir)
    {
        using var key = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\JesusKpekpes");
        key.SetValue("DisplayName", "Jesus Kpekpes");
        key.SetValue("Publisher", "Jesus Kpekpes");
        key.SetValue("DisplayVersion", Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0");
        key.SetValue("InstallLocation", dir);
        key.SetValue("UninstallString", "\"" + exe + "\" --uninstall");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        long bytes = 0;
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) bytes += new FileInfo(file).Length;
        key.SetValue("EstimatedSize", (int)(bytes / 1024), RegistryValueKind.DWord);
    }
}
