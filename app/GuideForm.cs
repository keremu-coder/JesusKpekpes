using System.Reflection;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace JesusKpekpes;

sealed class GuideForm : Form
{
    readonly List<AudioDevice> _devices;
    readonly ListBox _list = new();
    readonly Label _title = new();
    readonly Label _step = new();
    readonly Label _body = new();
    readonly Button _next = new();
    readonly Button _back = new();
    readonly CheckBox _windows = new();
    readonly NotifyIcon _tray = new();
    int _page;
    Live? _live;
    WasapiOut? _preview;
    MMDevice? _previewDevice;
    bool _exit;
    readonly bool _resume;

    public GuideForm(bool resume)
    {
        _resume = resume;
        _devices = Devices.Render();
        Text = "Jesus Kpekpes";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(460, 560);
        BackColor = Color.FromArgb(7, 9, 15);
        ForeColor = Color.FromArgb(242, 243, 245);
        Font = new Font("Segoe UI", 11f);

        _title.SetBounds(28, 24, 400, 36);
        _title.Font = new Font("Segoe UI", 20f, FontStyle.Bold);
        _title.Text = "Jesus Kpekpes";
        _step.SetBounds(28, 68, 400, 24);
        _step.ForeColor = Color.FromArgb(139, 124, 255);
        _body.SetBounds(28, 108, 404, 90);
        _body.ForeColor = Color.FromArgb(180, 184, 198);
        _list.SetBounds(28, 210, 404, 220);
        _list.BackColor = Color.FromArgb(18, 20, 28);
        _list.ForeColor = Color.FromArgb(242, 243, 245);
        _list.BorderStyle = BorderStyle.None;
        _list.IntegralHeight = false;
        foreach (var device in _devices) _list.Items.Add(device);
        var selected = _devices.FindIndex(d => d.Id == Devices.DefaultId());
        if (_list.Items.Count > 0) _list.SelectedIndex = Math.Max(0, selected);

        _windows.SetBounds(28, 444, 404, 28);
        _windows.Text = "Start with Windows";
        _windows.Checked = true;
        _windows.Visible = false;

        Style(_back, "Back", 28, 492, 120);
        Style(_next, "Next", 252, 492, 180);
        _back.Click += (_, _) => ShowPage(_page - 1);
        _next.Click += (_, _) => OnNext();

        Controls.AddRange([_title, _step, _body, _list, _windows, _back, _next]);
        _tray.Icon = SystemIcons.Application;
        _tray.Text = "Jesus Kpekpes";
        _tray.DoubleClick += (_, _) => { Show(); WindowState = FormWindowState.Normal; Activate(); };
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => { Show(); WindowState = FormWindowState.Normal; });
        menu.Items.Add("Exit", null, (_, _) => { _exit = true; Close(); });
        _tray.ContextMenuStrip = menu;
        ShowPage(0);
        if (_resume) BeginInvoke(TryResume);
    }

    AudioDevice? Selected => _list.SelectedItem as AudioDevice;

    void ShowPage(int page)
    {
        _page = Math.Clamp(page, 0, 2);
        _back.Visible = _page > 0;
        _list.Visible = _page == 0;
        _windows.Visible = _page == 2;
        StopPreview();
        if (_page == 0)
        {
            _step.Text = "Step 1 of 3";
            _body.Text = "These are your headphones. If the highlight is right, press Next.";
            _next.Text = "Next";
        }
        else if (_page == 1)
        {
            _step.Text = "Step 2 of 3";
            _body.Text = "A step plays, then a shot. The step should come through clearer than the shot.";
            _next.Text = "Next";
            PlayPreview();
        }
        else
        {
            _step.Text = "Step 3 of 3";
            var game = GameDevice();
            if (Selected is null)
            {
                _body.Text = "Go back and choose the headphones.";
                _next.Enabled = false;
            }
            else if (game is null)
            {
                _body.Text = "Jesus Kpekpes is not in the playback list yet. Pick the headphones now. Turn on starts when that device is there.";
                _next.Text = "Turn on";
                _next.Enabled = false;
            }
            else
            {
                _body.Text = $"The game uses {Engine.DeviceName}. You hear it in {Selected.Name}. Nothing else sits in between.";
                _next.Text = _live is null ? "Turn on" : "Turn off";
                _next.Enabled = true;
            }
        }
    }

    void OnNext()
    {
        if (_page < 2)
        {
            if (_page == 0 && Selected is null) return;
            ShowPage(_page + 1);
            return;
        }
        if (GameDevice() is null || Selected is null) return;
        if (_live is null) TurnOn();
        else TurnOff();
    }

    AudioDevice? GameDevice() =>
        _devices.FirstOrDefault(d => d.Name.Contains(Engine.DeviceName, StringComparison.OrdinalIgnoreCase) && d.Id != Selected?.Id);

    void TurnOn()
    {
        var phones = Selected;
        var game = GameDevice();
        if (phones is null || game is null) return;
        _live?.Dispose();
        _live = new Live(game.Id, phones.Id);
        _live.Start();
        Remember(phones.Id, true);
        if (_windows.Checked) Startup.Enable();
        else Startup.Disable();
        _next.Text = "Turn off";
        _body.Text = $"On. Set the game to {Engine.DeviceName}. You hear it in {phones.Name}. You can close this window.";
        _tray.Visible = true;
    }

    void TurnOff()
    {
        _live?.Dispose();
        _live = null;
        _tray.Visible = false;
        Remember(Selected?.Id, false);
        _next.Text = "Turn on";
    }

    void TryResume()
    {
        var saved = Remembered();
        if (saved.On != true || saved.Headphones is null) return;
        var phones = _devices.FirstOrDefault(d => d.Id == saved.Headphones);
        if (phones is null) return;
        _list.SelectedItem = phones;
        var game = GameDevice();
        if (game is null) return;
        _live = new Live(game.Id, phones.Id);
        _live.Start();
        ShowPage(2);
        _body.Text = $"On. The game uses {Engine.DeviceName}. You hear it in {phones.Name}.";
        WindowState = FormWindowState.Minimized;
    }

    void PlayPreview()
    {
        if (Selected is null) return;
        const int sr = 48000;
        const int n = sr * 2;
        var input = Enumerable.Range(0, 8).Select(_ => new float[n]).ToArray();
        for (var i = sr / 2; i < sr / 2 + 120; i++)
        {
            var env = MathF.Sin((i - sr / 2) / 120f * MathF.PI);
            input[Engine.Sl][i] = MathF.Sin(2 * MathF.PI * 3200 * i / sr) * 0.25f * env;
        }
        for (var i = sr; i < sr + (int)(sr * 0.15); i++)
            input[Engine.L][i] = MathF.Sin(2 * MathF.PI * 90 * i / sr) * 0.8f;
        var left = new float[n];
        var right = new float[n];
        new Engine(sr).Process(input, left, right);
        var interleaved = new byte[n * 8];
        for (var i = 0; i < n; i++)
        {
            BitConverter.TryWriteBytes(interleaved.AsSpan(i * 8, 4), left[i] * 0.4f);
            BitConverter.TryWriteBytes(interleaved.AsSpan(i * 8 + 4, 4), right[i] * 0.4f);
        }
        var buffer = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(sr, 2)) { DiscardOnBufferOverflow = true };
        buffer.AddSamples(interleaved, 0, interleaved.Length);
        using var enumerator = new MMDeviceEnumerator();
        _previewDevice = enumerator.GetDevice(Selected.Id);
        _preview = new WasapiOut(_previewDevice, AudioClientShareMode.Shared, true, (int)Math.Max(1, Selected.PeriodMs));

        _preview.Init(buffer);
        _preview.Play();
    }

    void StopPreview()
    {
        if (_preview is not null)
        {
            var preview = _preview;
            _preview = null;
            preview.Stop();
            preview.Dispose();
        }
        _previewDevice?.Dispose();
        _previewDevice = null;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_live is not null && !_exit)
        {
            e.Cancel = true;
            Hide();
            _tray.Visible = true;
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        StopPreview();
        _live?.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        base.OnFormClosed(e);
    }

    static void Style(Button button, string text, int x, int y, int width)
    {
        button.Text = text;
        button.SetBounds(x, y, width, 40);
        button.FlatStyle = FlatStyle.Flat;
        button.BackColor = Color.FromArgb(88, 101, 242);
        button.ForeColor = Color.White;
        button.FlatAppearance.BorderSize = 0;
    }

    static void Remember(string? headphones, bool on)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JesusKpekpes");
        Directory.CreateDirectory(dir);
        File.WriteAllLines(Path.Combine(dir, "settings.txt"),
        [
            "headphones=" + headphones,
            "on=" + (on ? "1" : "0"),
        ]);
    }

    static (string? Headphones, bool? On) Remembered()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JesusKpekpes", "settings.txt");
        if (!File.Exists(path)) return (null, null);
        var map = File.ReadAllLines(path)
            .Select(line => line.Split('=', 2))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0], p => p[1]);
        map.TryGetValue("headphones", out var headphones);
        map.TryGetValue("on", out var on);
        return (headphones, on == "1");
    }
}

static class Startup
{
    static string Link => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Jesus Kpekpes.lnk");

    public static void Enable()
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null || Environment.ProcessPath is null) return;
        var shell = Activator.CreateInstance(shellType);
        var shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, [Link]);
        if (shortcut is null) return;
        var kind = shortcut.GetType();
        kind.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, [Environment.ProcessPath]);
        kind.InvokeMember("Arguments", BindingFlags.SetProperty, null, shortcut, ["--on"]);
        kind.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, [AppContext.BaseDirectory]);
        kind.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
    }

    public static void Disable()
    {
        if (File.Exists(Link)) File.Delete(Link);
    }
}
