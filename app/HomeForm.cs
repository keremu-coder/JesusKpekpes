namespace JesusKpekpes;

sealed class HomeForm : Form
{
    public HomeForm()
    {
        Text = "Jesus Kpekpes";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(460, 680);
        BackColor = Color.FromArgb(7, 9, 15);
        ForeColor = Color.FromArgb(242, 243, 245);
        Font = new Font("Segoe UI", 11f);

        var title = new Label
        {
            Text = "Jesus Kpekpes",
            Font = new Font("Segoe UI", 20f, FontStyle.Bold),
            Bounds = new Rectangle(28, 24, 400, 36),
        };
        var body = new Label
        {
            Text = "Audio is its own switch. The PC tune does not touch it, and turning audio off does not undo the PC.",
            ForeColor = Color.FromArgb(180, 184, 198),
            Bounds = new Rectangle(28, 78, 404, 70),
        };
        var audio = Button("Audio tune", 150, () => Open(ref _audio, () => new GuideForm(false)));
        var pc = Button("Optimize the PC", 214, () => Open(ref _pc, () => new PcForm()));
        var programs = Button("Get the audio programs", 278, () => { Open(ref _programs, () => new ProgramsForm()); });
        var cod = Button("Call of Duty settings", 342, () => Open(ref _cod, () => new CodForm()));
        var check = Button("Sound check (compare two tunes)", 406, () => Open(ref _check, () => new SoundCheckForm()));
        var bench = Button("Monitor and frames", 470, () => Open(ref _bench, () => new BenchForm()));
        var startup = Button("Startup menu", 534, () => Open(ref _startup, () => new StartupForm()));
        Controls.AddRange([title, body, audio, pc, programs, cod, check, bench, startup]);
        // The audio tune cannot work without Equalizer APO. Offer it once per start, only when it is missing.
        Shown += (_, _) =>
        {
            if (AudioKit.Installed(AudioKit.Tools[0])) return;
            BeginInvoke(() =>
            {
                var form = Open(ref _programs, () => new ProgramsForm());
                ((ProgramsForm)form).StartAll();
            });
        };
    }

    Form? _audio, _pc, _programs, _cod, _check, _bench, _startup;

    // One window of each kind. Pressing the button again brings the open one forward instead of stacking another.
    T Open<T>(ref T? slot, Func<T> make) where T : Form
    {
        if (slot is { IsDisposed: false } open)
        {
            if (!open.Visible) open.Show();
            if (open.WindowState == FormWindowState.Minimized) open.WindowState = FormWindowState.Normal;
            open.Activate();
            return open;
        }
        var form = make();
        slot = form;
        form.Show(this);
        return form;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        foreach (var form in new[] { _audio, _pc, _programs, _cod, _check, _bench, _startup })
            if (form is { IsDisposed: false }) form.Dispose();
        base.OnFormClosed(e);
    }

    static Button Button(string text, int y, Action click)
    {
        var button = new Button
        {
            Text = text,
            Bounds = new Rectangle(28, y, 404, 48),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(88, 101, 242),
            ForeColor = Color.White,
        };
        button.FlatAppearance.BorderSize = 0;
        button.Click += (_, _) => click();
        return button;
    }
}

sealed class ProgramsForm : Form
{
    readonly Label _status = new();
    readonly ProgressBar _bar = new();
    readonly Button _all = new();
    readonly List<Button> _gets = [];
    bool _busy;

    public ProgramsForm()
    {
        Text = "Audio programs";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(520, 520);
        BackColor = Color.FromArgb(7, 9, 15);
        ForeColor = Color.FromArgb(242, 243, 245);
        Font = new Font("Segoe UI", 10f);
        _status.SetBounds(20, 12, 480, 64);
        _status.ForeColor = Color.FromArgb(180, 184, 198);
        Controls.Add(_status);
        _bar.SetBounds(20, 80, 480, 12);
        _bar.Style = ProgressBarStyle.Marquee;
        _bar.MarqueeAnimationSpeed = 30;
        _bar.Visible = false;
        Controls.Add(_bar);
        var y = 104;
        foreach (var tool in AudioKit.Tools)
        {
            var row = tool;
            var open = new Button
            {
                Text = "Open page",
                Bounds = new Rectangle(360, y, 140, 30),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(88, 101, 242),
                ForeColor = Color.White,
            };
            open.FlatAppearance.BorderSize = 0;
            open.Click += (_, _) => AudioKit.Open(row);
            Controls.Add(open);
            if (Installer.CanInstall(row.Name))
            {
                var get = new Button
                {
                    Text = "Download + install",
                    Bounds = new Rectangle(360, y + 32, 140, 30),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Color.FromArgb(40, 120, 80),
                    ForeColor = Color.White,
                };
                get.FlatAppearance.BorderSize = 0;
                get.Click += async (_, _) => await Install([row.Name]);
                Controls.Add(get);
                _gets.Add(get);
            }
            y += 72;
        }
        _all.Text = "Install what is missing";
        _all.Bounds = new Rectangle(20, 460, 240, 40);
        _all.FlatStyle = FlatStyle.Flat;
        _all.BackColor = Color.FromArgb(40, 120, 80);
        _all.ForeColor = Color.White;
        _all.FlatAppearance.BorderSize = 0;
        _all.Click += async (_, _) => await InstallMissing();
        Controls.Add(_all);
        var again = new Button
        {
            Text = "Check again",
            Bounds = new Rectangle(280, 460, 160, 40),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(88, 101, 242),
            ForeColor = Color.White,
        };
        again.FlatAppearance.BorderSize = 0;
        again.Click += (_, _) => RefreshStatus();
        Controls.Add(again);
        RefreshStatus();
    }

    /// <summary>Starts the download of everything that is missing without waiting for a click.</summary>
    public void StartAll() => _ = InstallMissing();

    Task InstallMissing()
    {
        var missing = AudioKit.Tools.Where(t => Installer.CanInstall(t.Name) && !AudioKit.Installed(t)).Select(t => t.Name).ToList();
        if (missing.Count == 0)
        {
            RefreshStatus("Everything we can install for you is already installed.");
            return Task.CompletedTask;
        }
        return Install(missing);
    }

    async Task Install(List<string> names)
    {
        if (_busy) return;
        _busy = true;
        _all.Enabled = false;
        foreach (var get in _gets) get.Enabled = false;
        _bar.Visible = true;
        try
        {
            var done = new System.Text.StringBuilder();
            var progress = new Progress<string>(text => _status.Text = text);
            foreach (var name in names)
            {
                var message = await Installer.Fetch(name, progress, question =>
                    MessageBox.Show(this, question, "Unchecked file", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes);
                done.AppendLine(name + ": " + message);
                _status.Text = done.ToString();
                if (names.Count > 1 && name != names[^1])
                {
                    _status.Text = done + "Finish that setup. The next one starts when this one is closed.";
                    await Task.Delay(1500);
                }
            }
            RefreshStatus(done.ToString().Trim());
        }
        finally
        {
            _bar.Visible = false;
            _all.Enabled = true;
            foreach (var get in _gets) get.Enabled = true;
            _busy = false;
        }
    }

    void RefreshStatus(string? message = null)
    {
        var labels = Controls.OfType<Label>().Where(l => l != _status).ToList();
        foreach (var label in labels) Controls.Remove(label);
        var y = 104;
        foreach (var tool in AudioKit.Tools)
        {
            var have = AudioKit.Installed(tool);
            var label = new Label
            {
                Text = (have ? "Installed  " : "Missing    ") + tool.Name + "\n" + tool.Why,
                Bounds = new Rectangle(20, y, 330, 60),
                ForeColor = have ? Color.FromArgb(140, 220, 160) : Color.FromArgb(242, 243, 245),
            };
            Controls.Add(label);
            y += 72;
        }
        _status.Text = message ?? "Press Install what is missing. It downloads, checks, then opens each setup for you. After Equalizer APO, restart Windows.";
    }
}
