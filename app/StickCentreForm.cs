using System.Runtime.InteropServices;

namespace JesusKpekpes;

/// <summary>
/// Explicitly operated stick-centre UI. Controller access occurs only during a requested test,
/// apply, or restore, plus the requested one-shot auto-apply on open/HID arrival. No timer polls.
/// </summary>
sealed class StickCentreForm : Form
{
    private const int WmDeviceChange = 0x0219;
    private const int DbtDeviceArrival = 0x8000;
    private const int DbtDevTypeDeviceInterface = 5;
    private const int DeviceNotifyWindowHandle = 0;
    private static readonly Guid HidInterfaceGuid = new("4D1E55B2-F16F-11CF-88CB-001111000030");
    private static readonly string BackupDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "JesusKpekpes", "stick-centre-backups");
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "JesusKpekpes", "stick-centre.txt");

    // Package A supplies the short-lived link factory when that package lands.
    internal static Func<IControllerLink?>? OpenController { get; set; }

    private readonly Label _status = new();
    private readonly CheckBox _autoApply = new() { Text = "Auto-apply when this window opens or the controller reconnects" };
    private readonly Button _test = new() { Text = "Test (hands off for 5 seconds)" };
    private readonly Button _apply = new() { Text = "Apply my centre" };
    private readonly Button _restore = new() { Text = "Restore from backup…" };
    private readonly Label _backup = new();
    private IntPtr _notification;
    private string? _lastBackupPath;
    private bool _busy;

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceBroadcastDeviceInterface
    {
        public int Size;
        public int DeviceType;
        public int Reserved;
        public Guid ClassGuid;
        public char Name;
    }

    public StickCentreForm()
    {
        Window.Prepare(this);
        Text = "Stick centre";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(560, 340);
        Font = new Font("Segoe UI", 10.5f);

        var title = new Label
        {
            Text = "Stick centre",
            Font = new Font("Segoe UI", 19f, FontStyle.Bold),
            Bounds = new Rectangle(24, 18, 500, 34),
        };
        var explanation = new Label
        {
            Text = "USB only. Keep both sticks untouched during the test. Apply changes only in memory; nothing is saved into the controller.",
            Bounds = new Rectangle(24, 62, 510, 48),
            ForeColor = Color.FromArgb(180, 184, 198),
        };
        _autoApply.SetBounds(24, 116, 510, 28);
        _autoApply.Checked = ReadAutoApply();
        _autoApply.CheckedChanged += (_, _) => SaveSettings();
        Style(_test, 24, 156, 240);
        Style(_apply, 280, 156, 240);
        Style(_restore, 24, 204, 240);
        _test.Click += async (_, _) => await RunOperation(Operation.Test);
        _apply.Click += async (_, _) => await RunOperation(Operation.Apply);
        _restore.Click += (_, _) => PickAndRestore();
        _backup.SetBounds(280, 207, 252, 38);
        _backup.ForeColor = Color.FromArgb(180, 184, 198);
        _backup.Text = "No backup selected";
        _status.SetBounds(24, 258, 510, 65);
        _status.ForeColor = Color.FromArgb(180, 184, 198);
        _status.Text = "Ready. No controller access occurs until an action runs.";
        Controls.AddRange([title, explanation, _autoApply, _test, _apply, _restore, _backup, _status]);

        _lastBackupPath = ReadLastBackup();
        UpdateBackupLabel();
        Shown += (_, _) =>
        {
            if (_autoApply.Checked) _ = RunOperation(Operation.Apply, automatic: true);
        };
    }

    private enum Operation { Test, Apply, Restore }

    private async Task RunOperation(Operation operation, bool automatic = false, string? restorePath = null)
    {
        if (_busy || IsDisposed) return;
        if (operation == Operation.Restore && string.IsNullOrWhiteSpace(restorePath)) return;
        _busy = true;
        SetButtons(false);
        _status.Text = operation switch
        {
            Operation.Test => "Testing. Leave both sticks untouched for five seconds…",
            Operation.Apply when automatic => "Auto-applying the saved live centre. No controller storage is changed…",
            Operation.Apply => "Measuring and applying a small live centre correction…",
            _ => "Restoring the selected backup in memory…",
        };
        try
        {
            var message = await Task.Run(() => RunBackend(operation, automatic, restorePath));
            if (IsDisposed) return;
            _status.Text = message.Status;
            if (!string.IsNullOrWhiteSpace(message.BackupPath))
            {
                _lastBackupPath = message.BackupPath;
                SaveSettings();
                UpdateBackupLabel();
            }
        }
        catch (Exception ex)
        {
            if (!IsDisposed) _status.Text = "Operation stopped: " + ex.Message;
        }
        finally
        {
            _busy = false;
            if (!IsDisposed) SetButtons(true);
        }
    }

    private static (string Status, string? BackupPath) RunBackend(Operation operation, bool automatic, string? restorePath)
    {
        var factory = OpenController;
        if (factory is null)
            return ("Controller access is not wired in this build yet. The UI will use the HID link when package A is integrated.", null);

        using var link = factory();
        if (link is null)
            return ("No supported DualSense controller is available over USB. Connect it by USB and try again.", null);

        var assembly = typeof(StickCentreForm).Assembly;
        if (operation == Operation.Test)
        {
            var testType = assembly.GetType("JesusKpekpes.StickTest");
            var run = testType?.GetMethod("Run", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            if (run is null) return ("The read-only test is not available in this build yet; package B has not been integrated.", null);
            var result = run.Invoke(null, [link]);
            var summary = ReadProperty(result, "PlainLanguage") ?? "The stick test returned no summary.";
            return (summary, null);
        }

        var liveType = assembly.GetType("JesusKpekpes.LiveCentre");
        if (liveType is null)
            return ("Live centre apply/restore is not available in this build yet; package C has not been integrated.", null);

        object? operationResult;
        if (operation == Operation.Apply)
        {
            var apply = liveType.GetMethod("Apply", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            if (apply is null) return ("The live apply entry point is unavailable.", null);
            operationResult = apply.Invoke(null, [link, BackupDirectory]);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(restorePath)) return ("Choose a backup file to restore.", null);
            var restore = liveType.GetMethod("Restore", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            if (restore is null) return ("The live restore entry point is unavailable.", null);
            operationResult = restore.Invoke(null, [link, restorePath, BackupDirectory]);
        }

        var message = ReadProperty(operationResult, "Message") ?? "The live centre operation returned no status.";
        var backupPath = ReadProperty(operationResult, "BackupPath");
        return (message, backupPath);
    }

    private void PickAndRestore()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Choose a stick-centre backup",
            Filter = "Stick-centre backup (*.json)|*.json|All files (*.*)|*.*",
            CheckFileExists = true,
            InitialDirectory = File.Exists(_lastBackupPath) ? Path.GetDirectoryName(_lastBackupPath)! : BackupDirectory,
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            _ = RunOperation(Operation.Restore, restorePath: dialog.FileName);
    }

    private void RegisterForHidArrival()
    {
        if (_notification != IntPtr.Zero || !IsHandleCreated) return;
        var filter = new DeviceBroadcastDeviceInterface
        {
            Size = Marshal.SizeOf<DeviceBroadcastDeviceInterface>(),
            DeviceType = DbtDevTypeDeviceInterface,
            ClassGuid = HidInterfaceGuid,
        };
        _notification = RegisterDeviceNotification(Handle, ref filter, DeviceNotifyWindowHandle);
        if (_notification == IntPtr.Zero)
            _status.Text = "Ready. Windows could not register for device-arrival notifications; open the window again after reconnecting.";
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        RegisterForHidArrival();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmDeviceChange && m.WParam.ToInt32() == DbtDeviceArrival && m.LParam != IntPtr.Zero)
        {
            try
            {
                var deviceType = Marshal.ReadInt32(m.LParam, sizeof(int));
                if (deviceType == DbtDevTypeDeviceInterface && Marshal.PtrToStructure<DeviceBroadcastDeviceInterface>(m.LParam).ClassGuid == HidInterfaceGuid
                    && _autoApply.Checked && !_busy && IsHandleCreated)
                {
                    BeginInvoke(() => _ = RunOperation(Operation.Apply, automatic: true));
                }
            }
            catch (Exception)
            {
                // An unexpected device-change structure must not interrupt the app's window procedure.
            }
        }
        base.WndProc(ref m);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        if (_notification != IntPtr.Zero)
        {
            UnregisterDeviceNotification(_notification);
            _notification = IntPtr.Zero;
        }
        base.OnHandleDestroyed(e);
    }

    private void SetButtons(bool enabled)
    {
        _test.Enabled = enabled;
        _apply.Enabled = enabled;
        _restore.Enabled = enabled;
    }

    private void UpdateBackupLabel() => _backup.Text = File.Exists(_lastBackupPath)
        ? "Latest backup: " + Path.GetFileName(_lastBackupPath)
        : "No backup selected";

    private void SaveSettings()
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllLines(SettingsPath,
            [
                "autoApply=" + (_autoApply.Checked ? "1" : "0"),
                "lastBackup=" + (_lastBackupPath ?? string.Empty),
                "version=1",
            ]);
        }
        catch (Exception)
        {
            _status.Text = "Settings could not be saved. Auto-apply will remain enabled only for this window.";
        }
    }

    private static bool ReadAutoApply()
    {
        try
        {
            var map = ReadSettings();
            return !map.TryGetValue("autoApply", out var value) || value != "0";
        }
        catch (Exception) { return true; }
    }

    private static string? ReadLastBackup()
    {
        try
        {
            var map = ReadSettings();
            return map.TryGetValue("lastBackup", out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
        }
        catch (Exception) { return null; }
    }

    private static Dictionary<string, string> ReadSettings()
    {
        if (!File.Exists(SettingsPath)) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        return File.ReadAllLines(SettingsPath)
            .Select(line => line.Split('=', 2))
            .Where(pair => pair.Length == 2)
            .ToDictionary(pair => pair[0], pair => pair[1], StringComparer.OrdinalIgnoreCase);
    }

    private static string? ReadProperty(object? value, string property) =>
        value?.GetType().GetProperty(property, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)?.GetValue(value)?.ToString();

    private static void Style(Button button, int x, int y, int width)
    {
        button.SetBounds(x, y, width, 38);
        button.FlatStyle = FlatStyle.Flat;
        button.BackColor = Color.FromArgb(88, 101, 242);
        button.ForeColor = Color.White;
        button.FlatAppearance.BorderSize = 0;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr RegisterDeviceNotification(IntPtr recipient, ref DeviceBroadcastDeviceInterface notificationFilter, int flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterDeviceNotification(IntPtr handle);
}
