namespace JesusKpekpes;

sealed class BenchForm : Form
{
    readonly TextBox _game = new() { Text = "cod.exe" };
    readonly TextBox _out = new();
    bool _busy;

    public BenchForm()
    {
        Window.Prepare(this);
        Text = "Monitor and frames";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(720, 560);
        BackColor = Color.FromArgb(7, 9, 15);
        ForeColor = Color.FromArgb(242, 243, 245);
        Font = new Font("Segoe UI", 10f);
        var note = new Label
        {
            Text = "The rate only moves to one Windows already lists, at this resolution. Frame numbers come from a one-minute capture. They are not a guess.",
            Bounds = new Rectangle(16, 12, 688, 40),
            ForeColor = Color.FromArgb(180, 184, 198),
        };
        var game = new Label { Text = "Game", Bounds = new Rectangle(16, 64, 48, 28) };
        _game.SetBounds(68, 60, 160, 28);
        _game.BackColor = Color.FromArgb(18, 20, 28);
        _game.ForeColor = Color.FromArgb(242, 243, 245);
        _game.BorderStyle = BorderStyle.FixedSingle;
        Add("Raise the monitor", 250, 56, Raise, 200);
        Add("Record before", 16, 108, () => _ = Measure("Before"), 160);
        Add("Record after", 190, 108, () => _ = Measure("After"), 160);
        _out.SetBounds(16, 164, 688, 376);
        _out.Multiline = true;
        _out.ReadOnly = true;
        _out.ScrollBars = ScrollBars.Vertical;
        _out.BackColor = Color.FromArgb(18, 20, 28);
        _out.ForeColor = Color.FromArgb(242, 243, 245);
        _out.BorderStyle = BorderStyle.None;
        _out.Font = new Font("Consolas", 9f);
        _out.Text = "Start the game and stay in a match. Record before, change what you want to change, then record after the same way. Average, 1% low and 0.1% low are the displayed frames from that minute. One pair is a hint, not proof. Undo on the PC window puts the old refresh rate back.";
        Controls.Add(note);
        Controls.Add(game);
        Controls.Add(_game);
        Controls.Add(_out);
    }

    void Add(string text, int x, int y, Action click, int width)
    {
        var button = new Button
        {
            Text = text,
            Bounds = new Rectangle(x, y, width, 36),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(88, 101, 242),
            ForeColor = Color.White,
        };
        button.FlatAppearance.BorderSize = 0;
        button.Click += (_, _) => click();
        Controls.Add(button);
    }

    void Raise()
    {
        if (MessageBox.Show(this, "Raise the monitor to the highest rate Windows already lists at this resolution? A rate that is not in the list is not invented. Undo on the PC window puts the old rate back.", "Monitor", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
        _ = Go("-Action Display", 60000);
    }

    async Task Measure(string side)
    {
        var game = _game.Text.Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(game, @"^[A-Za-z0-9._-]+\.exe$"))
        {
            _out.Text = "Type the game as a name like cod.exe. Nothing was measured.";
            return;
        }
        if (MessageBox.Show(this, "Stay in a match for one minute. " + side + " will be measured from the displayed frames of " + game + ". Do not close the game.", side, MessageBoxButtons.YesNo) != DialogResult.Yes) return;
        await Go("-Action Measure -Side " + side + " -Seconds 60 -ProcessName " + game, 150000);
    }

    async Task Go(string args, int timeoutMs)
    {
        if (_busy) { _out.Text = "Still working."; return; }
        _busy = true;
        _out.Text = "Working. Leave this window open.";
        try { _out.Text = await Task.Run(() => ScriptHost.Run(args, timeoutMs)); }
        finally { _busy = false; }
    }
}
