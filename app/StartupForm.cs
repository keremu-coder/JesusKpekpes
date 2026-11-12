namespace JesusKpekpes;

sealed class StartupForm : Form
{
    readonly CheckedListBox _list = new();
    readonly TextBox _out = new();
    bool _busy;

    public StartupForm()
    {
        Window.Prepare(this);
        Text = "Startup menu";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(720, 560);
        BackColor = Color.FromArgb(7, 9, 15);
        ForeColor = Color.FromArgb(242, 243, 245);
        Font = new Font("Segoe UI", 10f);
        var note = new Label
        {
            Text = "Tick a program to turn it off at startup. It is not deleted. Windows Security and this program stay on. Undo on the PC window turns the ones this window turned off back on.",
            Bounds = new Rectangle(16, 12, 688, 40),
            ForeColor = Color.FromArgb(180, 184, 198),
        };
        _list.SetBounds(16, 60, 688, 280);
        _list.CheckOnClick = true;
        _list.BackColor = Color.FromArgb(18, 20, 28);
        _list.ForeColor = Color.FromArgb(242, 243, 245);
        _list.BorderStyle = BorderStyle.None;
        _out.SetBounds(16, 400, 688, 144);
        _out.Multiline = true;
        _out.ReadOnly = true;
        _out.ScrollBars = ScrollBars.Vertical;
        _out.BackColor = Color.FromArgb(18, 20, 28);
        _out.ForeColor = Color.FromArgb(242, 243, 245);
        _out.BorderStyle = BorderStyle.None;
        _out.Font = new Font("Consolas", 9f);
        var refresh = new Button { Text = "Refresh", Bounds = new Rectangle(16, 352, 140, 36), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(88, 101, 242), ForeColor = Color.White };
        refresh.FlatAppearance.BorderSize = 0;
        refresh.Click += (_, _) => _ = LoadList();
        var off = new Button { Text = "Turn ticked ones off", Bounds = new Rectangle(170, 352, 200, 36), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(88, 101, 242), ForeColor = Color.White };
        off.FlatAppearance.BorderSize = 0;
        off.Click += (_, _) => _ = TurnOff();
        Controls.Add(note);
        Controls.Add(_list);
        Controls.Add(refresh);
        Controls.Add(off);
        Controls.Add(_out);
        Shown += (_, _) => _ = LoadList();
    }

    async Task LoadList()
    {
        if (_busy) return;
        _busy = true;
        _out.Text = "Reading the startup menu.";
        try
        {
            var text = await Task.Run(() => ScriptHost.Run("-Action Startup", 60000));
            _list.Items.Clear();
            var kept = new System.Text.StringBuilder();
            foreach (var raw in text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                var part = raw.Split('\t');
                if (part.Length < 4 || part[0] != "ITEM") { kept.AppendLine(raw); continue; }
                var name = part[1];
                if (name.Contains("SecurityHealth", StringComparison.OrdinalIgnoreCase) || name.Contains("Jesus Kpekpes", StringComparison.OrdinalIgnoreCase) || name.Contains("JesusKpekpes", StringComparison.OrdinalIgnoreCase) || name.Contains("WinDefend", StringComparison.OrdinalIgnoreCase))
                {
                    kept.AppendLine(name + " stays on.");
                    continue;
                }
                _list.Items.Add(name + "  (" + part[2] + ", " + part[3] + ")", false);
            }
            _out.Text = kept.Length == 0 ? "Tick what should not open with Windows, then press Turn ticked ones off." : kept.ToString();
        }
        finally { _busy = false; }
    }

    async Task TurnOff()
    {
        var names = new List<string>();
        foreach (var item in _list.CheckedItems)
        {
            var text = item?.ToString() ?? "";
            var name = text.Split("  (", 2)[0];
            if (name.Contains('"') || name.Contains('|')) { _out.Text = name + " was skipped because the name is not safe to pass on."; return; }
            if (name.Length > 0) names.Add(name);
        }
        if (names.Count == 0) { _out.Text = "Nothing was ticked, so nothing was turned off."; return; }
        if (MessageBox.Show(this, "Turn these off at startup? They stay installed.\r\n\r\n" + string.Join("\r\n", names), "Startup menu", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
        if (_busy) return;
        _busy = true;
        try { _out.Text = await Task.Run(() => ScriptHost.Run("-Action StartupOff -Names \"" + string.Join("|", names) + "\"", 60000)); }
        finally { _busy = false; }
        await LoadList();
    }
}
