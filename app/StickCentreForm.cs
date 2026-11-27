using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace JesusKpekpes;

/// <summary>
/// Explicitly operated stick-centre UI. Controller access occurs only during a requested test, apply, restore or glitch
/// capture, the requested one-shot auto-apply on open or HID arrival, and the optional live view. The live view is started
/// by the person, reads input reports only (no feature report is ever sent), and stops by itself when this window loses
/// focus or closes, so it cannot run while a game is in front. No timer runs unless the live view is on.
/// </summary>
sealed class StickCentreForm : Form
{
    private const int WmDeviceChange = 0x0219;
    private const int DbtDeviceArrival = 0x8000;
    private const int DbtDevTypeDeviceInterface = 5;
    private const int DeviceNotifyWindowHandle = 0;
    private const string LiveStartText = "Start live view";
    private const string LiveStopText = "Stop live view";
    private static readonly Guid HidInterfaceGuid = new("4D1E55B2-F16F-11CF-88CB-001111000030");
    private static readonly string BackupDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "JesusKpekpes", "stick-centre-backups");
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "JesusKpekpes", "stick-centre.txt");
    private static readonly Color Muted = Color.FromArgb(180, 184, 198);
    private static readonly Color CardColor = Color.FromArgb(18, 20, 28);
    private static readonly Color Good = Color.FromArgb(60, 216, 124);
    private static readonly Color Amber = Color.FromArgb(255, 176, 0);

    // Package A supplies the short-lived link factory when that package lands.
    internal static Func<IControllerLink?>? OpenController { get; set; }

    private readonly TextBox _status = new();
    private readonly CheckBox _autoApply = new() { Text = "Auto-apply when this window opens or the controller reconnects (only if the pad is untouched)" };
    private readonly Button _test = new() { Text = "Test (hands off for 5 seconds)" };
    private readonly Button _apply = new() { Text = "Apply my centre" };
    private readonly Button _restore = new() { Text = "Restore from backup…" };
    private readonly Button _glitch = new() { Text = "Glitch capture (60 s, hands off)" };
    private readonly Button _live = new() { Text = LiveStartText };
    private readonly Button _centre = new() { Text = "Calibrate centre (hands off)" };
    private readonly Button _range = new() { Text = RangeStartText };
    private readonly Button _save = new() { Text = "Save permanently…" };
    private readonly Button _settle = new() { Text = "Settle test (push, let go ×10)" };
    private const string RangeStartText = "Calibrate range…";
    private const string RangeFinishText = "Finish range";
    private const string RangeRunningMarker = "Range calibration is running";
    private bool _rangeRunning;
    private readonly CheckBox _zoom = new() { Text = "Zoom ×16" };
    private readonly Label _rate = new();
    private readonly Label _backup = new();
    private readonly StickDial _leftDial = new();
    private readonly StickDial _rightDial = new();
    private readonly Label _leftName = new();
    private readonly Label _leftNumbers = new();
    private readonly Label _leftState = new();
    private readonly Label _rightName = new();
    private readonly Label _rightNumbers = new();
    private readonly Label _rightState = new();
    private readonly System.Windows.Forms.Timer _liveTimer = new() { Interval = 33 };
    private CancellationTokenSource? _liveCts;
    private Task? _liveTask;
    private string? _liveNote;
    private int _lx = 128;
    private int _ly = 128;
    private int _rx = 128;
    private int _ry = 128;
    private long _reports;
    private long _lastReports;
    private long _lastRateAt;
    private const int LiveMeanSamples = 48;
    private readonly object _meanGate = new();
    private readonly int[] _meanLx = new int[LiveMeanSamples];
    private readonly int[] _meanLy = new int[LiveMeanSamples];
    private readonly int[] _meanRx = new int[LiveMeanSamples];
    private readonly int[] _meanRy = new int[LiveMeanSamples];
    private int _meanCount;
    private int _meanAt;
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
        ClientSize = new Size(700, 790);
        BackColor = Color.FromArgb(7, 9, 15);
        ForeColor = Color.FromArgb(242, 243, 245);
        Font = new Font("Segoe UI", 10.5f);

        var title = new Label
        {
            Text = "Stick centre",
            Font = new Font("Segoe UI", 19f, FontStyle.Bold),
            Bounds = new Rectangle(24, 14, 652, 36),
        };
        var explanation = new Label
        {
            Text = "See the rest point in counts, and trim a wrong centre in the pad itself. USB only. The handle closes when you leave this window, so the game still reads the controller directly.",
            Bounds = new Rectangle(24, 52, 652, 42),
            ForeColor = Muted,
        };

        var card = new Panel { Bounds = new Rectangle(24, 98, 652, 292), BackColor = CardColor };
        BuildStickColumn(card, "Left stick", 40, _leftDial, _leftName, _leftNumbers, _leftState);
        BuildStickColumn(card, "Right stick", 412, _rightDial, _rightName, _rightNumbers, _rightState);
        _rate.SetBounds(252, 56, 148, 64);
        _rate.TextAlign = ContentAlignment.MiddleCenter;
        _rate.ForeColor = Muted;
        _rate.Text = "Reports per second" + Environment.NewLine + "—";
        Style(_live, new Rectangle(16, 250, 210, 36), primary: false);
        _zoom.SetBounds(240, 255, 130, 28);
        _zoom.ForeColor = Muted;
        var liveHint = new Label
        {
            Text = "Input reports only. Stops when you leave this window.",
            Bounds = new Rectangle(380, 250, 260, 38),
            ForeColor = Muted,
            Font = new Font("Segoe UI", 9f),
        };
        card.Controls.AddRange([_rate, _live, _zoom, liveHint]);
        ShowIdle();

        Style(_test, new Rectangle(24, 404, 318, 40), primary: true);
        Style(_apply, new Rectangle(358, 404, 318, 40), primary: true);
        Style(_restore, new Rectangle(24, 452, 318, 40), primary: false);
        Style(_glitch, new Rectangle(358, 452, 318, 40), primary: false);
        Style(_centre, new Rectangle(24, 500, 318, 40), primary: true);
        Style(_range, new Rectangle(358, 500, 318, 40), primary: true);
        Style(_save, new Rectangle(24, 548, 318, 40), primary: false);
        Style(_settle, new Rectangle(358, 548, 318, 40), primary: true);
        _save.BackColor = Color.FromArgb(200, 120, 20);
        _autoApply.SetBounds(24, 596, 652, 40);
        _autoApply.ForeColor = Muted;
        _autoApply.Checked = ReadAutoApply();
        _autoApply.CheckedChanged += (_, _) => SaveSettings();
        _backup.SetBounds(24, 638, 652, 22);
        _backup.ForeColor = Muted;
        _backup.Text = "No backup selected";
        _status.SetBounds(24, 666, 652, 112);
        _status.Multiline = true;
        _status.ReadOnly = true;
        _status.ScrollBars = ScrollBars.Vertical;
        _status.BorderStyle = BorderStyle.None;
        _status.BackColor = CardColor;
        _status.ForeColor = Color.FromArgb(242, 243, 245);
        _status.Font = new Font("Segoe UI", 10f);
        Say("Ready. No controller access occurs until you press a button.");

        _live.Click += async (_, _) =>
        {
            if (_liveTask is null) StartLive();
            else await StopLiveAsync("Live view stopped. Nothing was sent to the controller.");
        };
        _zoom.CheckedChanged += (_, _) =>
        {
            var factor = _zoom.Checked ? 16f : 1f;
            _leftDial.Zoom = factor;
            _rightDial.Zoom = factor;
        };
        _liveTimer.Tick += (_, _) => LiveTick();
        _zoom.Checked = true;
        _glitch.Click += async (_, _) => await RunOperation(Operation.Glitch);
        _centre.Click += async (_, _) => await RunOperation(Operation.Centre);
        _settle.Click += async (_, _) => await RunOperation(Operation.Settle);
        _save.Click += async (_, _) =>
        {
            var answer = MessageBox.Show(this,
                "This writes the controller's current stick calibration into its permanent storage. It stays after you unplug it, and it is not undone by unplugging." + Environment.NewLine + Environment.NewLine +
                "A backup of the current values is kept first. Only do this after the stick already reads well (press Test first)." + Environment.NewLine + Environment.NewLine +
                "Do not unplug the controller or close this window while it saves. Save now?",
                "Save into the controller?", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer == DialogResult.Yes) await RunOperation(Operation.Save);
        };
        _range.Click += async (_, _) => await RunOperation(_rangeRunning ? Operation.RangeFinish : Operation.RangeBegin);
        _test.Click += async (_, _) => await RunOperation(Operation.Test);
        _apply.Click += async (_, _) => await RunOperation(Operation.Apply);
        _restore.Click += (_, _) => PickAndRestore();

        Controls.AddRange([title, explanation, card, _test, _apply, _restore, _glitch, _centre, _range, _save, _settle, _autoApply, _backup, _status]);

        _lastBackupPath = ReadLastBackup();
        UpdateBackupLabel();
        Shown += (_, _) =>
        {
            if (_autoApply.Checked) _ = RunOperation(Operation.Apply, automatic: true);
        };
    }

    private enum Operation { Test, Apply, Restore, Glitch, Centre, RangeBegin, RangeFinish, Save, Settle }

    private static void BuildStickColumn(Panel card, string name, int left, StickDial dial, Label caption, Label numbers, Label state)
    {
        dial.Bounds = new Rectangle(left + 20, 12, 160, 160);
        caption.Text = name;
        caption.Font = new Font("Segoe UI", 11f, FontStyle.Bold);
        caption.TextAlign = ContentAlignment.MiddleCenter;
        caption.Bounds = new Rectangle(left, 176, 200, 24);
        numbers.Font = new Font("Consolas", 10.5f);
        numbers.TextAlign = ContentAlignment.MiddleCenter;
        numbers.Bounds = new Rectangle(left, 200, 200, 24);
        state.TextAlign = ContentAlignment.MiddleCenter;
        state.Bounds = new Rectangle(left, 224, 200, 24);
        card.Controls.AddRange([dial, caption, numbers, state]);
    }

    private void ShowIdle()
    {
        foreach (var (dial, numbers, state) in new[] { (_leftDial, _leftNumbers, _leftState), (_rightDial, _rightNumbers, _rightState) })
        {
            dial.SetPosition(0f, 0f, false);
            numbers.Text = "X —     Y —";
            state.Text = "Live view is off";
            state.ForeColor = Muted;
        }
    }

    // ---- live view: input reports only, started by the person, stops by itself ----

    private void StartLive()
    {
        if (_liveTask is not null || _busy || IsDisposed) return;
        var factory = OpenController;
        if (factory is null)
        {
            Say("Controller access is not available in this build.");
            return;
        }
        _liveNote = null;
        Interlocked.Exchange(ref _reports, 0);
        _lastReports = 0;
        _lastRateAt = Environment.TickCount64;
        lock (_meanGate) { _meanCount = 0; _meanAt = 0; }
        var cts = new CancellationTokenSource();
        _liveCts = cts;
        _live.Text = LiveStopText;
        Say("Live view is on. It reads the controller's own reports and sends nothing. Click another window and it stops by itself.");
        _liveTask = Task.Run(() => LiveLoop(factory, cts.Token));
        _liveTimer.Start();
    }

    // Runs on a worker thread only while the live view is on. It reads input reports and nothing else.
    private void LiveLoop(Func<IControllerLink?> factory, CancellationToken token)
    {
        string? problem = null;
        IControllerLink? link = null;
        try
        {
            link = factory();
            if (link is null)
            {
                problem = "No DualSense or DualSense Edge is connected by USB. Bluetooth is refused: the centre trim is a USB feature report, and Bluetooth adds radio delay. Use a data cable.";
                return;
            }
            if (!link.IsUsb)
            {
                problem = "This controller is on Bluetooth, so it was not read. Plug in a data USB cable. DS4Windows can stay open.";
                return;
            }
            var lastGood = Stopwatch.GetTimestamp();
            while (!token.IsCancellationRequested)
            {
                var report = link.ReadInputReport(50);
                if (report is null || report.Length < 5 || report[0] != 0x01)
                {
                    if (report is { Length: > 0 } && report[0] == 0x31)
                    {
                        problem = "This report is a Bluetooth report, so the live view stopped. Plug in a data USB cable.";
                        return;
                    }
                    if (Stopwatch.GetElapsedTime(lastGood).TotalSeconds > 2)
                    {
                        problem = "No USB reports arrived for 2 seconds. Is the controller still connected by a data cable?";
                        return;
                    }
                    continue;
                }
                lastGood = Stopwatch.GetTimestamp();
                Volatile.Write(ref _lx, report[1]);
                Volatile.Write(ref _ly, report[2]);
                Volatile.Write(ref _rx, report[3]);
                Volatile.Write(ref _ry, report[4]);
                PushMean(report[1], report[2], report[3], report[4]);
                Interlocked.Increment(ref _reports);
            }
        }
        catch (Exception ex)
        {
            problem = "Live view stopped: " + ex.Message;
        }
        finally
        {
            try { link?.Dispose(); } catch (Exception) { /* the handle is closed either way */ }
            var note = problem ?? _liveNote ?? "Live view stopped. Nothing was sent to the controller.";
            PostToUi(() => FinishLive(note));
        }
    }

    private async Task StopLiveAsync(string note)
    {
        var task = _liveTask;
        if (task is null) return;
        _liveNote = note;
        try { _liveCts?.Cancel(); } catch (ObjectDisposedException) { }
        try { await task.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (Exception) { /* the loop reports its own problems through FinishLive */ }
        FinishLive(note);
    }

    private void FinishLive(string note)
    {
        if (_liveTask is null) return;
        _liveTimer.Stop();
        _liveTask = null;
        _liveCts?.Dispose();
        _liveCts = null;
        _live.Text = LiveStartText;
        _rate.Text = "Reports per second" + Environment.NewLine + "—";
        ShowIdle();
        if (!IsDisposed) Say(note);
    }

    private void LiveTick()
    {
        var mean = Mean();
        UpdateStick(_leftDial, _leftNumbers, _leftState, Volatile.Read(ref _lx), Volatile.Read(ref _ly), mean.Lx, mean.Ly, mean.Count);
        UpdateStick(_rightDial, _rightNumbers, _rightState, Volatile.Read(ref _rx), Volatile.Read(ref _ry), mean.Rx, mean.Ry, mean.Count);
        var now = Environment.TickCount64;
        if (now - _lastRateAt < 1000) return;
        var total = Interlocked.Read(ref _reports);
        var perSecond = (total - _lastReports) * 1000.0 / (now - _lastRateAt);
        _lastReports = total;
        _lastRateAt = now;
        _rate.Text = "Reports per second" + Environment.NewLine + perSecond.ToString("0", CultureInfo.InvariantCulture);
    }

    private static void UpdateStick(StickDial dial, Label numbers, Label state, int rawX, int rawY, float meanX, float meanY, int samples)
    {
        var x = (rawX - StickMath.Rest) / StickMath.Rest;
        var y = (rawY - StickMath.Rest) / StickMath.Rest;
        var mx = (meanX - StickMath.Rest) / StickMath.Rest;
        var my = (meanY - StickMath.Rest) / StickMath.Rest;
        dial.SetPosition(x, y, true, mx, my);
        var cx = meanX - StickMath.Rest;
        var cy = meanY - StickMath.Rest;
        numbers.Text = samples < 8
            ? "learning the rest point"
            : "X " + cx.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture) + " c   Y " + cy.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture) + " c";
        var radial = MathF.Sqrt(cx * cx + cy * cy);
        if (samples < 8)
        {
            state.Text = "Reading";
            state.ForeColor = Muted;
        }
        else if (radial > 18f)
        {
            state.Text = "Stick is being moved";
            state.ForeColor = Muted;
        }
        else if (radial < 1f)
        {
            state.Text = "On a centre code";
            state.ForeColor = Good;
        }
        else
        {
            state.Text = "Off by " + radial.ToString("0.00", CultureInfo.InvariantCulture) + " counts";
            state.ForeColor = Amber;
        }
    }

    private void PushMean(int lx, int ly, int rx, int ry)
    {
        lock (_meanGate)
        {
            _meanLx[_meanAt] = lx;
            _meanLy[_meanAt] = ly;
            _meanRx[_meanAt] = rx;
            _meanRy[_meanAt] = ry;
            _meanAt = (_meanAt + 1) % LiveMeanSamples;
            if (_meanCount < LiveMeanSamples) _meanCount++;
        }
    }

    private (float Lx, float Ly, float Rx, float Ry, int Count) Mean()
    {
        lock (_meanGate)
        {
            if (_meanCount == 0) return (StickMath.Rest, StickMath.Rest, StickMath.Rest, StickMath.Rest, 0);
            double lx = 0, ly = 0, rx = 0, ry = 0;
            for (var i = 0; i < _meanCount; i++)
            {
                lx += _meanLx[i];
                ly += _meanLy[i];
                rx += _meanRx[i];
                ry += _meanRy[i];
            }
            var n = _meanCount;
            return ((float)(lx / n), (float)(ly / n), (float)(rx / n), (float)(ry / n), n);
        }
    }

    private void PostToUi(Action action)
    {
        try
        {
            if (!IsDisposed && IsHandleCreated) BeginInvoke(action);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // The window is already closing. The worker has closed the controller either way.
        }
    }

    // Clicking another window, such as the game, stops the live view. It can never run while you play.
    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (_liveTask is not null)
            _ = StopLiveAsync("Live view stopped because you left this window. It cannot run while another window, such as your game, is in front.");
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _liveNote = null;
        try { _liveCts?.Cancel(); } catch (ObjectDisposedException) { }
        _liveTimer.Stop();
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _liveTimer.Dispose();
        base.OnFormClosed(e);
    }

    // ---- test, apply, restore, glitch capture ----

    private async Task RunOperation(Operation operation, bool automatic = false, string? restorePath = null)
    {
        if (_busy || IsDisposed) return;
        if (operation == Operation.Restore && string.IsNullOrWhiteSpace(restorePath)) return;
        _busy = true;
        SetButtons(false);
        try
        {
            // Only one thing may hold the controller at a time. The live view steps aside first.
            if (_liveTask is not null) await StopLiveAsync("Live view stopped.");
            if (IsDisposed) return;
            Say(operation switch
            {
                Operation.Test => "Testing. Leave both sticks untouched for five seconds…",
                Operation.Glitch => "Watching for 60 seconds. Put the controller down and do not touch it…",
                Operation.Apply when automatic => "Auto-applying the saved live centre. No controller storage is changed…",
                Operation.Apply => "Measuring and applying a small live centre correction…",
                Operation.Centre => "Calibrating the centre. Keep both sticks and the controller completely still for about three seconds…",
                Operation.RangeBegin => "Starting range calibration…",
                Operation.RangeFinish => "Finishing range calibration…",
                Operation.Settle => "Starting the settle test…",
                Operation.Save => "Saving into the controller. Do not unplug it and do not close this window…",
                _ => "Restoring the selected backup in memory…",
            });
            var message = await Task.Run(() => RunBackend(operation, automatic, restorePath, text => PostToUi(() => Say(text))));
            if (IsDisposed) return;
            Say(message.Status);
            if (operation == Operation.RangeBegin) _rangeRunning = message.Status.StartsWith(RangeRunningMarker, StringComparison.Ordinal);
            else if (operation == Operation.RangeFinish) _rangeRunning = false;
            _range.Text = _rangeRunning ? RangeFinishText : RangeStartText;
            if (!string.IsNullOrWhiteSpace(message.BackupPath))
            {
                _lastBackupPath = message.BackupPath;
                SaveSettings();
                UpdateBackupLabel();
            }
        }
        catch (Exception ex)
        {
            if (!IsDisposed) Say("Operation stopped: " + ex.Message);
        }
        finally
        {
            _busy = false;
            if (!IsDisposed) SetButtons(true);
            // Range calibration needs the person to turn the sticks, so the live view starts by itself to show them doing it.
            if (!IsDisposed && operation == Operation.RangeBegin && _rangeRunning) StartLive();
        }
    }

    private static (string Status, string? BackupPath) RunBackend(Operation operation, bool automatic, string? restorePath, Action<string>? progress = null)
    {
        var factory = OpenController;
        if (factory is null)
            return ("Controller access is not available in this build.", null);

        IControllerLink? opened;
        try { opened = factory(); }
        catch (Exception ex) { return (ex.Message, null); }
        using var link = opened;
        if (link is null)
            return ("No DualSense or DualSense Edge is connected by USB. Bluetooth is refused: the centre trim is a USB feature report, and Bluetooth adds radio delay. Use a data cable.", null);
        if (!link.IsUsb)
            return ("This controller is on Bluetooth, so nothing was written. Plug in a data USB cable. DS4Windows can stay open.", null);

        if (automatic)
        {
            // Nothing that runs by itself may send anything to a pad that is being used.
            var quiet = PadQuiet.Check(link);
            if (!quiet.Quiet) return (quiet.Message, null);
        }

        if (operation == Operation.Glitch) return (GlitchTest.Run(link).PlainLanguage, null);
        if (operation == Operation.Centre)
        {
            var run = StickCalibration.CalibrateCentre(link, BackupDirectory);
            return (run.Message, run.BackupPath);
        }
        if (operation == Operation.RangeBegin)
        {
            var run = StickCalibration.BeginRange(link, BackupDirectory);
            return (run.Message, run.BackupPath);
        }
        if (operation == Operation.RangeFinish) return (StickCalibration.FinishRange(link).Message, null);
        if (operation == Operation.Settle) return (SettleTest.Run(link, progress), null);
        if (operation == Operation.Save)
        {
            var run = StickSave.Save(link, BackupDirectory);
            return (run.Message, run.BackupPath);
        }
        if (operation == Operation.Test) return (StickTest.Run(link).PlainLanguage, null);

        if (operation == Operation.Apply)
        {
            var applied = LiveCentre.Apply(link, BackupDirectory);
            return (applied.Message, applied.BackupPath);
        }

        if (string.IsNullOrWhiteSpace(restorePath)) return ("Choose a backup file to restore.", null);
        var restored = LiveCentre.Restore(link, restorePath, BackupDirectory);
        return (restored.Message, restored.BackupPath);
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
            Say("Ready. Windows could not register for device-arrival notifications; open the window again after reconnecting.");
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
                    && HidRules.IsUsbDualSensePath(Marshal.PtrToStringUni(IntPtr.Add(m.LParam, 28)))
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
        // While a range calibration is running, only "Finish range" may be pressed: nothing else may talk to the pad.
        var normal = enabled && !_rangeRunning;
        _test.Enabled = normal;
        _apply.Enabled = normal;
        _restore.Enabled = normal;
        _glitch.Enabled = normal;
        _centre.Enabled = normal;
        _save.Enabled = normal;
        _settle.Enabled = normal;
        _live.Enabled = enabled;
        _range.Enabled = enabled;
    }

    // The result box needs Windows line breaks, and some messages arrive with plain ones.
    private void Say(string text) => _status.Text = text.Replace("\r\n", "\n").Replace("\n", "\r\n");

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
            Say("Settings could not be saved. Auto-apply will remain enabled only for this window.");
        }
    }

    private static bool ReadAutoApply()
    {
        try
        {
            var map = ReadSettings();
            return map.TryGetValue("autoApply", out var value) && value == "1";
        }
        catch (Exception) { return false; }
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

    private static void Style(Button button, Rectangle bounds, bool primary)
    {
        button.Bounds = bounds;
        button.FlatStyle = FlatStyle.Flat;
        button.BackColor = primary ? Color.FromArgb(88, 101, 242) : Color.FromArgb(36, 40, 56);
        button.ForeColor = Color.White;
        button.FlatAppearance.BorderSize = 0;
        button.Cursor = Cursors.Hand;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr RegisterDeviceNotification(IntPtr recipient, ref DeviceBroadcastDeviceInterface notificationFilter, int flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterDeviceNotification(IntPtr handle);
}
