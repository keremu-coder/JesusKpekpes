namespace JesusKpekpes;

sealed class PcForm : Form
{
    readonly TextBox _out = new();
    readonly Label _status = new();
    readonly ProgressBar _bar = new();
    readonly System.Windows.Forms.Timer _clock = new() { Interval = 1000 };
    readonly List<Button> _buttons = [];
    DateTime _started;
    string _what = "";
    string _latest = "";
    bool _busy;

    public PcForm()
    {
        Window.Prepare(this);
        Text = "PC tune";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(720, 640);
        BackColor = Color.FromArgb(7, 9, 15);
        ForeColor = Color.FromArgb(242, 243, 245);
        Font = new Font("Segoe UI", 10f);

        var note = new Label
        {
            Text = "This does not touch audio. Undo here does not turn the audio tune off.",
            Bounds = new Rectangle(16, 12, 680, 24),
            ForeColor = Color.FromArgb(180, 184, 198),
        };
        _status.SetBounds(16, 196, 688, 22);
        _status.ForeColor = Color.FromArgb(139, 124, 255);
        _bar.SetBounds(16, 220, 688, 6);
        _bar.Style = ProgressBarStyle.Marquee;
        _bar.MarqueeAnimationSpeed = 25;
        _bar.Visible = false;
        _clock.Tick += (_, _) => ShowStatus();
        _out.SetBounds(16, 232, 688, 392);
        _out.Multiline = true;
        _out.ReadOnly = true;
        _out.ScrollBars = ScrollBars.Vertical;
        _out.BackColor = Color.FromArgb(18, 20, 28);
        _out.ForeColor = Color.FromArgb(242, 243, 245);
        _out.BorderStyle = BorderStyle.None;
        _out.Font = new Font("Consolas", 9f);
        _out.Text = PlainPreview();

        var y = 44;
        Add("What will change", 16, y, () => _ = Go("-Action Optimize -Module Both -WhatIf", "Checking what would change", (t, _) => PlainPreview() + "\r\n\r\n" + PcReport.Normalize(t)));
        Add("Optimize PC and controller", 210, y, () => _ = Go("-Action Optimize -Module Both", "Optimizing your PC and controller", (t, code) => PcReport.Optimized(t, code), 900000, true), 210);
        Add("Undo that", 440, y, () =>
        {
            if (MessageBox.Show(this, "Put the PC and controller changes back? Audio stays as it is.", "Undo", MessageBoxButtons.YesNo) == DialogResult.Yes)
                _ = Go("-Action Undo -Module Both -Silent", "Putting your settings back", (t, _) => string.IsNullOrWhiteSpace(t) ? "Done. Your PC and controller settings are back as they were. Audio was not touched." : PcReport.Normalize(t));
        });
        y = 96;
        Add("Check this PC", 16, y, () => _ = Go("-Action Verify -Module Both", "Checking your PC (read only)", (t, _) => PcReport.Verify(t, CodFind.Report())));
        Add("NVIDIA panel", 176, y, OpenNvidia);
        Add("Display settings", 336, y, () => Open("ms-settings:display"));
        Add("Network check", 516, y, () => _ = LoadCheck());
        Add("Name the hitch", 16, 148, () => _ = Go("-Action Hitch", "Watching for stutter. Play your game for 20 seconds. Nothing is changed", (t, _) => PcReport.Normalize(t)), 220, Color.FromArgb(139, 124, 255));
        Controls.Add(note);
        Controls.Add(_status);
        Controls.Add(_bar);
        Controls.Add(_out);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _clock.Stop();
        _clock.Dispose();
        base.OnFormClosed(e);
    }

    void Add(string text, int x, int y, Action click, int width = 150, Color? back = null)
    {
        var button = new Button
        {
            Text = text,
            Bounds = new Rectangle(x, y, width, 40),
            FlatStyle = FlatStyle.Flat,
            BackColor = back ?? Color.FromArgb(88, 101, 242),
            ForeColor = Color.White,
        };
        button.FlatAppearance.BorderSize = 0;
        button.Click += (_, _) => click();
        Controls.Add(button);
        _buttons.Add(button);
    }

    static string PlainPreview() =>
        """
        What will change if you press Optimize. Nothing has been written yet.

        - Power plan to full performance, USB and PCIe power saving off
        - Game Mode, Game Bar and Game DVR off. Hardware GPU scheduling on
        - A 0.5 ms timer
        - Wired adapter power saving off
        - SysMain, DiagTrack and Windows Search disabled
        - Windows Update will not install drivers or reboot while you are logged in
        - Xbox apps removed
        - High priority for the game
        - Controller timer and strafe profile, only if DS4Windows is there
        - Secure Boot stays on
        - Audio is not changed

        - NVIDIA Power management mode = Prefer maximum performance for Call of Duty. Undo puts the old value back.
        - If NVIDIA's interface is missing, nothing is written. Add cod.exe and set Power management mode to Prefer maximum performance by hand.
        - If Windows is running a monitor below the highest rate it already lists at this resolution, that rate is raised. The resolution is not changed. A rate that is not in Windows' list is not invented. Undo puts the old rate back.
        The check can see Resizable BAR from the driver. It cannot flip the BIOS switch.

        Startup programs are not turned off here. Use Startup menu, and tick only the ones you want off. They are not deleted.

        Name the hitch only watches for about 20 seconds and prints the driver or process behind the longest stall. It does not change any setting and it does not turn that driver off.
        Frame numbers are not estimated here. Monitor and frames records a minute of the game and counts average, 1% low and 0.1% low from that capture.
        """;

    void Show(string text) => _out.Text = string.IsNullOrWhiteSpace(text) ? "No report came back." : PcReport.Normalize(text);

    void ShowStatus()
    {
        if (!_busy) return;
        var seconds = (int)(DateTime.Now - _started).TotalSeconds;
        _status.Text = _what + "... " + seconds / 60 + ":" + (seconds % 60).ToString("00") + (_latest.Length > 0 ? "   " + _latest : "");
    }

    void Busy(bool on, string what = "")
    {
        _busy = on;
        _what = what;
        _latest = "";
        _started = DateTime.Now;
        _bar.Visible = on;
        foreach (var button in _buttons) button.Enabled = !on;
        if (on) { _clock.Start(); ShowStatus(); }
        else { _clock.Stop(); _status.Text = ""; }
    }

    async Task Go(string args, string what, Func<string, int, string> wrap, int timeoutMs = 180000, bool live = false)
    {
        if (_busy) return;
        Busy(true, what);
        _out.Text = "Working. You can leave this window open. The line above shows it is still running.";
        try
        {
            var started = true;
            void Line(string line)
            {
                if (string.IsNullOrWhiteSpace(line) || IsDisposed || !IsHandleCreated) return;
                BeginInvoke(() =>
                {
                    _latest = line.Trim();
                    if (!live) return;
                    if (started) { _out.Clear(); started = false; }
                    _out.AppendText(line.TrimEnd() + "\r\n");
                });
            }
            var (text, code) = await Task.Run(() => Run(args, timeoutMs, Line));
            Show(wrap(text, code));
        }
        finally
        {
            Busy(false);
        }
    }

    static (string Text, int Code) Run(string args, int timeoutMs, Action<string> line)
    {
        var script = Script();
        if (!File.Exists(script)) return ("The PC script was not found next to the program. Reinstall or unzip the whole folder.", 1);
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
            if (process is null) return ("Windows did not start PowerShell.", 1);
            var all = new System.Text.StringBuilder();
            void Take(string? data)
            {
                if (data is null) return;
                lock (all) all.AppendLine(data);
                line(data);
            }
            process.OutputDataReceived += (_, e) => Take(e.Data);
            process.ErrorDataReceived += (_, e) => Take(e.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(true); } catch (InvalidOperationException) { }
                lock (all) return ("It did not finish in " + timeoutMs / 60000 + " minutes, so it was stopped. Nothing is left running. What it printed before that:\r\n" + all, 1);
            }
            process.WaitForExit();
            lock (all) return (all.ToString().Trim(), process.ExitCode);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return (ex.Message, 1);
        }
    }

    async Task LoadCheck()
    {
        if (_busy) return;
        Busy(true, "Checking your internet line");
        _out.Text = "This takes about 30 seconds. Please wait.";
        try { Show(await NetCheck.Run()); }
        catch (Exception ex) { Show(ex.Message); }
        finally { Busy(false); }
    }

    static void Open(string target) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true });

    void OpenNvidia()
    {
        foreach (var path in new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "Control Panel Client", "nvcplui.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "NVIDIA Corporation", "Control Panel Client", "nvcplui.exe"),
        })
        {
            if (!File.Exists(path)) continue;
            Open(path);
            return;
        }
        Show("NVIDIA Control Panel is not on this PC. If your graphics card is not NVIDIA (for example AMD Radeon), ignore this: nothing is wrong and nothing needs doing.\r\n\r\nIf you do have an NVIDIA card, press Optimize. If NVIDIA's panel is still missing, add cod.exe and set Power management mode to Prefer maximum performance by hand.");
    }

    static string Script()
    {
        // Only the copy next to the program. The app runs as administrator, so it must never pick up a script from elsewhere.
        return Path.Combine(AppContext.BaseDirectory, "pc", "Kpekpes.ps1");
    }
}
