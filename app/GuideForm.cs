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
    readonly Button _remove = new();
    readonly TrackBar _loud = new() { Minimum = 0, Maximum = 100, Value = 100, TickFrequency = 25 };
    readonly TrackBar _room = new() { Minimum = 0, Maximum = 100, Value = 100, TickFrequency = 25 };
    readonly Label _loudLabel = new();
    readonly Label _roomLabel = new();
    readonly ComboBox _preset = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    readonly CheckBox _keep = new() { Text = "Keep my other audio setup", Checked = true };
    readonly System.Windows.Forms.Timer _save = new() { Interval = 400 };
    readonly NotifyIcon _tray = new();
    int _page;
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
        _seven.Text = "7.1, untested";
        _seven.AutoSize = true;
        _seven.Location = new Point(210, 356);
        _keep.SetBounds(28, 400, 404, 28);
        Style(_remove, "Remove completely", 28, 440, 404);
        _remove.Visible = false;
        _remove.Click += (_, _) => RemoveAll();
        _preset.Visible = false;
        _seven.Visible = false;
        _keep.Visible = false;
        _preset.SelectedIndexChanged += (_, _) => ScheduleSave();
        _seven.CheckedChanged += (_, _) => { RefreshRoute(); ScheduleSave(); };
        _save.Tick += (_, _) => { _save.Stop(); if (_on && Selected is not null) TurnOn(); };

        Style(_back, "Back", 28, 492, 120);
        Style(_next, "Next", 252, 492, 180);
        _back.Click += (_, _) => ShowPage(_page - 1);
        _next.Click += (_, _) => OnNext();

        Controls.AddRange([_title, _step, _body, _list, _loudLabel, _loud, _roomLabel, _room, _preset, _seven, _keep, _remove, _back, _next]);
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
        _remove.Visible = mix;
        _keep.Visible = mix;
        _seven.Enabled = Selected is { Channels: >= 8 };
        if (!_seven.Enabled && _seven.Checked) _seven.Checked = false;
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
            _body.Text = "A loud step, then a quiet room. A shot stays on its own side. This preview is stereo. Rear channels are not played.";
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
                RefreshRoute();
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

    void RefreshRoute()
    {
        if (_page != 2 || Selected is null) return;
        _body.Text = _seven.Checked
            ? "7.1 is advanced and untested. The centre speaker and the LFE are muted. The fold into left and right was not measured. This preview does not play the rear channels."
            : $"Stereo on {Selected.Name}. The game can stay at 2 channels.";
    }

    void ScheduleSave()
    {
        _loudLabel.Text = "Footstep hit  " + _loud.Value;
        _roomLabel.Text = "Surround quiet  " + _room.Value;
        if (!_on) return;
        _save.Stop();
        _save.Start();
    }

    MixPlan Current(AudioDevice phones) => new(
        _preset.SelectedIndex == 1 ? Preset.Well : Preset.Drop,
        _seven.Checked ? Route.Surround : Route.Stereo,
        _loud.Value,
        _room.Value,
        Devices.ApoSelector(phones.Id));

    WriteResult Commit(AudioDevice phones, bool keep)
    {
        var plan = Current(phones);
        if (Admin.IsElevated()) return InPlace.Apply(phones.Id, plan, keep);
        var preset = plan.Preset == Preset.Well ? "well" : "drop";
        var route = plan.Route == Route.Surround ? "surround" : "stereo";
        var code = Admin.Run($"--apply \"{phones.Id}\" {preset} {route} {plan.Hit} {plan.Quiet} {(keep ? "1" : "0")}");
        return code switch
        {
            0 => InPlace.Attached(phones.Id) ? WriteResult.On : WriteResult.NotAttached,
            2 => WriteResult.Conflict,
            3 => WriteResult.Missing,
            _ => WriteResult.Denied,
        };
    }

    void TurnOn()
    {
        var phones = Selected;
        if (phones is null) return;
        if (_seven.Checked && phones.Channels < 8)
        {
            _seven.Checked = false;
            _body.Text = $"{phones.Name} is not 7.1. Stereo is on instead.";
        }
        var found = InPlace.Found();
        var result = Commit(phones, _keepOthers);
        _on = result == WriteResult.On;
        Remember(phones.Id, _on);
        _next.Text = _on ? "Turn off" : "Turn on";
        _body.Text = result switch
        {
            WriteResult.On when found.Count > 0 => $"On. {string.Join(", ", found)} stays. Turn this off to use that instead.",
            WriteResult.On => $"On. Leave the game on {phones.Name}.",
            WriteResult.NotAttached when found.Count > 0 => $"Not on. {string.Join(", ", found)} stays. Open DeviceSelector and tick {phones.Name}. Not a cable.",
            WriteResult.NotAttached => $"Not on. Open DeviceSelector and tick {phones.Name}. Not a microphone, and not a virtual cable. Then press Turn on again.",
            WriteResult.Conflict => "HeSuVi, Rara, or Peace is already there. Press Turn on again to keep that setup.",
            WriteResult.Denied => "Windows blocked the write. Press Yes if it asks.",
            _ => "Equalizer APO is not installed. Secure Boot stays on.",
        };
        if (result == WriteResult.Conflict) _keepOthers = true;
        _tray.Visible = _on;
    }

    void TurnOff()
    {
        var result = Admin.IsElevated() ? InPlace.Clear() : Admin.Run("--clear") == 0 ? WriteResult.On : WriteResult.Denied;
        if (result != WriteResult.On)
        {
            _body.Text = "Windows blocked the undo. The tune was not confirmed off. Press Yes if it asks.";
            return;
        }
        _on = false;
        _keepOthers = false;
        _tray.Visible = false;
        Remember(Selected?.Id, false);
        _next.Text = "Turn on";
        _body.Text = "Off. The other setup was not removed.";
    }

    void RemoveAll()
    {
        if (MessageBox.Show(this, "Remove the Jesus Kpekpes files? Rara, HeSuVi, and Equalizer APO stay.", "Remove", MessageBoxButtons.YesNo) != DialogResult.Yes)
            return;
        var result = Admin.IsElevated() ? InPlace.Remove() : Admin.Run("--remove") == 0 ? WriteResult.On : WriteResult.Denied;
        if (result != WriteResult.On)
        {
            _body.Text = "Windows blocked the remove. Nothing was confirmed.";
            return;
        }
        _on = false;
        _tray.Visible = false;
        Remember(Selected?.Id, false);
        _next.Text = "Turn on";
        _body.Text = "Removed. Rara, HeSuVi, and Equalizer APO were left in place.";
    }

    void TryResume()
    {
        var saved = Remembered();
        if (saved.On != true || saved.Headphones is null) return;
        var phones = _devices.FirstOrDefault(d => d.Id == saved.Headphones);
        if (phones is null) return;
        _list.SelectedItem = phones;
        if (phones.Channels < 8) _seven.Checked = false;
        _on = Commit(phones, true) == WriteResult.On;
        if (!_on) return;
        ShowPage(2);
        _body.Text = _seven.Checked
            ? $"On, but 7.1 is untested. The centre speaker is muted. The game stays on {phones.Name}."
            : $"On. The game stays on {phones.Name}.";
        WindowState = FormWindowState.Minimized;
    }

    void PlayPreview()
    {
        if (Selected is null) return;
        const int sr = 48000;
        const int n = sr * 2;
        var left = new float[n];
        var right = new float[n];
        for (var i = sr / 2; i < sr / 2 + 280; i++)
        {
            var env = MathF.Sin((i - sr / 2) / 280f * MathF.PI);
            var step = MathF.Sin(2 * MathF.PI * 180 * i / sr) * 0.04f * env;
            left[i] = step;
            right[i] = step * 0.2f;
        }
        for (var i = sr; i < sr + (int)(sr * 0.12); i++)
        {
            var env = MathF.Exp(-(i - sr) / (sr * 0.04f));
            right[i] += (MathF.Sin(2 * MathF.PI * 90 * i / sr) * 0.85f) * env;
        }
        Mix.Process(Current(Selected), left, right);
        var interleaved = new byte[n * 8];
        for (var i = 0; i < n; i++)
        {
            BitConverter.TryWriteBytes(interleaved.AsSpan(i * 8, 4), Math.Clamp(left[i] * 0.4f, -1f, 1f));
            BitConverter.TryWriteBytes(interleaved.AsSpan(i * 8 + 4, 4), Math.Clamp(right[i] * 0.4f, -1f, 1f));
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
        if (_on && !_exit)
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
        _loudLabel.Text = "Footstep hit  " + _loud.Value;
        _roomLabel.Text = "Surround quiet  " + _room.Value;
        return (headphones, on == "1");
    }
}
