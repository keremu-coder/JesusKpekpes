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
    readonly CheckBox _seven = new();
    readonly TrackBar _loud = new() { Minimum = 0, Maximum = 100, Value = 100, TickFrequency = 25 };
    readonly TrackBar _room = new() { Minimum = 0, Maximum = 100, Value = 100, TickFrequency = 25 };
    readonly Label _loudLabel = new();
    readonly Label _roomLabel = new();
    readonly ComboBox _preset = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    readonly System.Windows.Forms.Timer _save = new() { Interval = 400 };
    readonly NotifyIcon _tray = new();
    int _page;
    Live? _live;
    WasapiOut? _preview;
    MMDevice? _previewDevice;
    bool _exit;
    bool _keepOthers;
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

        _loudLabel.SetBounds(28, 200, 404, 22);
        _loudLabel.Text = "Footstep hit";
        _loud.SetBounds(28, 224, 404, 40);
        _roomLabel.SetBounds(28, 276, 404, 22);
        _roomLabel.Text = "Surround quiet";
        _room.SetBounds(28, 300, 404, 40);
        _loud.Visible = false;
        _room.Visible = false;
        _loudLabel.Visible = false;
        _roomLabel.Visible = false;
        _loud.Scroll += (_, _) => ScheduleSave();
        _room.Scroll += (_, _) => ScheduleSave();
        _preset.Items.AddRange(new object[] { "Drop", "Well" });
        _preset.SelectedIndex = 0;
        _preset.SetBounds(28, 352, 180, 32);
        _seven.Text = "7.1";
        _seven.SetBounds(230, 348, 160, 28);
        _preset.Visible = false;
        _seven.Visible = false;
        _preset.SelectedIndexChanged += (_, _) => ScheduleSave();
        _seven.CheckedChanged += (_, _) => ScheduleSave();
        _save.Tick += (_, _) => { _save.Stop(); if (_on && Selected is not null) TurnOn(); };

        Style(_back, "Back", 28, 492, 120);
        Style(_next, "Next", 252, 492, 180);
        _back.Click += (_, _) => ShowPage(_page - 1);
        _next.Click += (_, _) => OnNext();

        Controls.AddRange([_title, _step, _body, _list, _loudLabel, _loud, _roomLabel, _room, _preset, _seven, _back, _next]);
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
        var mix = _page == 2;
        _loud.Visible = mix;
        _room.Visible = mix;
        _loudLabel.Visible = mix;
        _roomLabel.Visible = mix;
        _preset.Visible = mix;
        _seven.Visible = mix;
        _seven.Enabled = Selected is { Channels: >= 8 };
        StopPreview();
        if (_page == 0)
        {
            _step.Text = "Step 1 of 3";
            _body.Text = "These are your headphones. If the highlight is right, press Next.";
            _next.Text = "Next";
            _next.Enabled = true;
        }
        else if (_page == 1)
        {
            _step.Text = "Step 2 of 3";
            _body.Text = "A step plays, then a shot. The step should come through clearer than the shot.";
            _next.Text = "Next";
            _next.Enabled = true;
            PlayPreview();
        }
        else
        {
            _step.Text = "Step 3 of 3";
            if (Selected is null)
            {
                _body.Text = "Go back and choose the headphones.";
                _next.Enabled = false;
            }
            else
            {
                _body.Text = _seven.Checked
                    ? $"7.1 on {Selected.Name}. The game must actually be set to 7.1."
                    : $"Stereo on {Selected.Name}. The game can stay at 2 channels.";
                _next.Text = _on ? "Turn off" : "Turn on";
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
        if (Selected is null) return;
        if (!_on) TurnOn();
        else TurnOff();
    }

    bool _on;

    void ScheduleSave()
    {
        _loudLabel.Text = "Footstep hit  " + _loud.Value;
        _roomLabel.Text = "Surround quiet  " + _room.Value;
        if (!_on) return;
        _save.Stop();
        _save.Start();
    }

    MixPlan Current(string name) => new(
        _preset.SelectedIndex == 1 ? Preset.Well : Preset.Drop,
        _seven.Checked ? Route.Surround : Route.Stereo,
        _loud.Value,
        _room.Value,
        name);

    void TurnOn()
    {
        var phones = Selected;
        if (phones is null) return;
        if (_seven.Checked && phones.Channels < 8)
        {
            _seven.Checked = false;
            _body.Text = $"{phones.Name} is not 7.1. Stereo is on instead.";
        }
        var result = InPlace.Apply(phones.Id, Current(phones.Name), _keepOthers);
        _on = result is WriteResult.On or WriteResult.NotAttached;
        Remember(phones.Id, _on);
        _next.Text = _on ? "Turn off" : "Turn on";
        _body.Text = result switch
        {
            WriteResult.On => $"On. Leave the game on {phones.Name}.",
            WriteResult.NotAttached => "The file is written. Equalizer APO is not turned on for these headphones yet.",
            WriteResult.Conflict => "HeSuVi, Rara, or Peace is already there. Press Turn on again to keep that setup.",
            WriteResult.Denied => "Windows blocked the write. Press Yes if it asks.",
            _ => "Equalizer APO is not installed. Secure Boot stays on.",
        };
        if (result == WriteResult.Conflict) _keepOthers = true;
        _tray.Visible = _on;
    }

    void TurnOff()
    {
        InPlace.Clear();
        _on = false;
        _keepOthers = false;
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
        _on = InPlace.Apply(phones.Id, Current(phones.Name), true) is WriteResult.On or WriteResult.NotAttached;
        if (!_on) return;
        ShowPage(2);
        _body.Text = $"On. The game stays on {phones.Name}.";
        WindowState = FormWindowState.Minimized;
    }

    void PlayPreview()
    {
        if (Selected is null) return;
        const int sr = 48000;
        const int n = sr * 2;
        var plan = Current(Selected.Name);
        var left = new float[n];
        var right = new float[n];
        var side = new float[n];
        for (var i = sr / 2; i < sr / 2 + 120; i++)
        {
            var env = MathF.Sin((i - sr / 2) / 120f * MathF.PI);
            var sample = MathF.Sin(2 * MathF.PI * 3200 * i / sr) * 0.25f * env;
            if (plan.Route == Route.Stereo) { left[i] = sample; right[i] = sample * 0.3f; }
            else side[i] = sample;
        }
        for (var i = sr; i < sr + (int)(sr * 0.15); i++)
            left[i] = right[i] = MathF.Sin(2 * MathF.PI * 90 * i / sr) * 0.8f;
        Mix.Process(plan, left, right, side, side);
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
        if ((_live is not null || _on) && !_exit)
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

    void Remember(string? headphones, bool on)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JesusKpekpes");
        Directory.CreateDirectory(dir);
        File.WriteAllLines(Path.Combine(dir, "settings.txt"),
        [
            "headphones=" + headphones,
            "on=" + (on ? "1" : "0"),
            "hit=" + _loud.Value,
            "quiet=" + _room.Value,
            "preset=" + (_preset.SelectedIndex == 1 ? "well" : "drop"),
            "route=" + (_seven.Checked ? "surround" : "stereo"),
        ]);
    }

    (string? Headphones, bool? On) Remembered()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JesusKpekpes", "settings.txt");
        if (!File.Exists(path)) return (null, null);
        var map = File.ReadAllLines(path)
            .Select(line => line.Split('=', 2))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0], p => p[1]);
        map.TryGetValue("headphones", out var headphones);
        map.TryGetValue("on", out var on);
        if (map.TryGetValue("hit", out var hit) && int.TryParse(hit, out var hitValue)) _loud.Value = Math.Clamp(hitValue, 0, 100);
        if (map.TryGetValue("quiet", out var quiet) && int.TryParse(quiet, out var quietValue)) _room.Value = Math.Clamp(quietValue, 0, 100);
        if (map.TryGetValue("preset", out var preset)) _preset.SelectedIndex = preset == "well" ? 1 : 0;
        if (map.TryGetValue("route", out var route)) _seven.Checked = route == "surround";
        return (headphones, on == "1");
    }
}
