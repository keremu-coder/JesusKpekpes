namespace JesusKpekpes;

sealed class PcForm : Form
{
    readonly TextBox _out = new();
    bool _busy;

    public PcForm()
    {
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
        _out.SetBounds(16, 200, 688, 424);
        _out.Multiline = true;
        _out.ReadOnly = true;
        _out.ScrollBars = ScrollBars.Vertical;
        _out.BackColor = Color.FromArgb(18, 20, 28);
        _out.ForeColor = Color.FromArgb(242, 243, 245);
        _out.BorderStyle = BorderStyle.None;
        _out.Font = new Font("Consolas", 9f);
        _out.Text = PlainPreview();

        var y = 44;
        Add("What will change", 16, y, () => _ = Go("-Action Optimize -Module Both -WhatIf", t => PlainPreview() + "\r\n\r\n" + t));
        Add("Optimize PC and controller", 210, y, () => _ = Go("-Action Optimize -Module Both"), 210);
        Add("Undo that", 440, y, () =>
        {
            if (MessageBox.Show(this, "Put the PC and controller changes back? Audio stays as it is.", "Undo", MessageBoxButtons.YesNo) == DialogResult.Yes)
                _ = Go("-Action Undo -Module Both -Silent");
        });
        y = 96;
        Add("Check this PC", 16, y, () => _ = Go("-Action Verify -Module Both", t => t + "\r\n\r\n" + CodFind.Report()));
        Add("NVIDIA panel", 176, y, OpenNvidia);
        Add("Display settings", 336, y, () => Open("ms-settings:display"));
        Add("Network check", 516, y, () => _ = LoadCheck());
        Add("Name the hitch", 16, 148, () => _ = Go("-Action Hitch", null, "Play for 20 seconds. This only watches. Nothing is changed."), 220, Color.FromArgb(139, 124, 255));
        Controls.Add(note);
        Controls.Add(_out);
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

    void Show(string text) => _out.Text = string.IsNullOrWhiteSpace(text) ? "No report came back." : text;

    async Task Go(string args, Func<string, string>? wrap = null, string? starting = null)
    {
        if (_busy)
        {
            Show("Still working.");
            return;
        }
        _busy = true;
        Show(starting ?? "Working. This can take a minute. The window stays usable.");
        try
        {
            var text = await Task.Run(() => Run(args));
            Show(wrap is null ? text : wrap(text));
        }
        finally
        {
            _busy = false;
        }
    }

    static string Run(string args)
    {
        var script = Script();
        if (!File.Exists(script)) return "The PC script was not found next to the program. Reinstall or unzip the whole folder.";
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
            if (process is null) return "Windows did not start PowerShell.";
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(180000)) return "That is taking too long. Close it from Task Manager if it is stuck.";
            return (output.Result + "\r\n" + error.Result).Trim();
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    async Task LoadCheck()
    {
        Show("Checking the line...");
        try { Show(await NetCheck.Run()); }
        catch (Exception ex) { Show(ex.Message); }
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
        Show("NVIDIA Control Panel was not found. Run Optimize to set Prefer maximum performance. If NVIDIA's interface is missing, add cod.exe and set Power management mode to Prefer maximum performance by hand.");
    }

    static string Script()
    {
        // Only the copy next to the program. The app runs as administrator, so it must never pick up a script from elsewhere.
        return Path.Combine(AppContext.BaseDirectory, "pc", "Kpekpes.ps1");
    }
}
