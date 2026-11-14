namespace JesusKpekpes;

sealed class HomeForm : Form
{
    readonly NotifyIcon _tray = new();
    readonly Label _ver = new();
    readonly Button _update;
    Updater.Found? _found;
    bool _updating;
    bool _quit;

    public HomeForm()
    {
        Window.Prepare(this);
        Text = "Jesus Kpekpes";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = true;
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
            Text = "One window. Minimize, or the X, hides it on the logo by the clock. It is not on the taskbar. Right-click that logo and choose Exit to quit.",
            ForeColor = Color.FromArgb(180, 184, 198),
            Bounds = new Rectangle(28, 78, 404, 70),
        };
        var audio = Button("Audio tune", 150, () => Open(new GuideForm(false)));
        var pc = Button("Optimize the PC", 214, () => Open(new PcForm()));
        var programs = Button("Get the audio programs", 278, () => Open(new ProgramsForm()));
        var cod = Button("Call of Duty settings", 342, () => Open(new CodForm()));
        var check = Button("Sound check (compare two tunes)", 406, () => Open(new SoundCheckForm()));
        var bench = Button("Monitor and frames", 470, () => Open(new BenchForm()));
        var startup = Button("Startup menu", 534, () => Open(new StartupForm()));
        _update = Button("Update", 592, StartUpdate);
        _update.Visible = false;
        _ver.Text = "Version " + Updater.Current().ToString(3);
        _ver.Font = new Font("Segoe UI", 8.5f);
        _ver.ForeColor = Color.FromArgb(196, 184, 165);
        _ver.Bounds = new Rectangle(28, 646, 404, 32);
        Controls.AddRange([title, body, audio, pc, programs, cod, check, bench, startup, _update, _ver]);
        _tray.Icon = Window.Mark;
        _tray.Text = "Jesus Kpekpes";
        _tray.Visible = true;
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => Reveal());
        menu.Items.Add("Exit", null, (_, _) => { _quit = true; Close(); });
        Look.Menu(menu);
        _tray.ContextMenuStrip = menu;
        _tray.MouseDoubleClick += (_, _) => Reveal();
        Shown += (_, _) => CheckForUpdate();
        Shown += (_, _) =>
        {
            if (AudioKit.Installed(AudioKit.Tools[0])) return;
            BeginInvoke(() =>
            {
                var form = new ProgramsForm();
                form.Shown += (_, _) => form.StartAll();
                Open(form);
            });
        };
    }

    /// Quiet on purpose: no network, no release, no answer means no button and no message.
    async void CheckForUpdate()
    {
        try
        {
            var current = Updater.Current();
            var note = Updater.TakeResult();
            if (note is not null) _ver.Text = note;
            if (!Updater.CanUpdate(current)) return;
            var dir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            _ = Task.Run(() => Updater.Tidy(dir));
            var found = await Updater.Check(current);
            if (found is null || IsDisposed) return;
            _found = found;
            _update.Text = "Update to version " + found.Version.ToString(3);
            _update.Visible = true;
            if (note is null) _ver.Text = "Version " + current.ToString(3) + ". Version " + found.Version.ToString(3) + " is ready.";
        }
        catch (Exception)
        {
            /* no answer is not an error */
        }
    }

    async void StartUpdate()
    {
        if (_found is not { } found || _updating) return;
        var ask = "Update to version " + found.Version.ToString(3) + "?\n\nJesus Kpekpes closes and opens again by itself. Your settings and the PC and audio changes stay as they are.";
        if (MessageBox.Show(this, ask, "Update", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        _updating = true;
        _update.Enabled = false;
        try
        {
            var dir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var staged = await Updater.Prepare(found, dir, new Progress<string>(text => _ver.Text = text));
            if (!staged.Ok)
            {
                _ver.Text = "The update did not install: " + staged.Message + " You are still on the old version.";
                return;
            }
            if (!Updater.StartFinish(staged, found.Version, dir))
            {
                _ver.Text = "The update could not be started. You are still on the old version.";
                return;
            }
            _quit = true;
            Close();
        }
        catch (Exception ex)
        {
            _ver.Text = "The update did not install: " + ex.Message + " You are still on the old version.";
        }
        finally
        {
            if (!_quit)
            {
                _updating = false;
                if (!IsDisposed) _update.Enabled = true;
            }
        }
    }

    void Open(Form form)
    {
        Hide();
        try
        {
            using (form) form.ShowDialog();
        }
        finally
        {
            if (!_quit && !IsDisposed) Reveal();
        }
    }

    void Reveal()
    {
        WindowState = FormWindowState.Normal;
        Show();
        Activate();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState == FormWindowState.Minimized) Hide();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_quit)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        _tray.Visible = false;
        _tray.Dispose();
        base.OnFormClosing(e);
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
        Window.Prepare(this);
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
        _all.Click += async (_, _) =>
        {
            if (_all.Text == "Done") { Close(); return; }
            await InstallMissing();
        };
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
            var text = done.ToString();
            if (!text.Contains("not opened") && !text.Contains("did not finish"))
                _all.Text = "Done";
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
