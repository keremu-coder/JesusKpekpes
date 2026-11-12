namespace JesusKpekpes;

sealed class CodForm : Form
{
    readonly TextBox _out = new();

    public CodForm()
    {
        Window.Prepare(this);
        Text = "Call of Duty settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(720, 600);
        BackColor = Color.FromArgb(7, 9, 15);
        ForeColor = Color.FromArgb(242, 243, 245);
        Font = new Font("Segoe UI", 10f);

        var note = new Label
        {
            Text = "Close the game first. Only a short list of settings is changed. Your original file is kept.",
            Bounds = new Rectangle(16, 12, 688, 24),
            ForeColor = Color.FromArgb(180, 184, 198),
        };
        _out.SetBounds(16, 100, 688, 484);
        _out.Multiline = true;
        _out.ReadOnly = true;
        _out.ScrollBars = ScrollBars.Vertical;
        _out.BackColor = Color.FromArgb(18, 20, 28);
        _out.ForeColor = Color.FromArgb(242, 243, 245);
        _out.BorderStyle = BorderStyle.None;
        _out.Font = new Font("Consolas", 9f);
        Add("What will change", 16, 48, () => Preview());
        Add("Apply", 236, 48, () => Apply());
        Add("Undo", 456, 48, () => Undo());
        Controls.AddRange([note, _out]);
        Preview();
    }

    void Add(string text, int x, int y, Action click)
    {
        var button = new Button
        {
            Text = text,
            Bounds = new Rectangle(x, y, 200, 40),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(88, 101, 242),
            ForeColor = Color.White,
        };
        button.FlatAppearance.BorderSize = 0;
        button.Click += (_, _) => click();
        Controls.Add(button);
    }

    void Preview()
    {
        var files = CodProfile.Files();
        if (files.Length == 0)
        {
            _out.Text = "No Call of Duty options file was found in Documents\\Call of Duty\\players.\r\nStart the game once, close it, and try again. Nothing was changed.";
            return;
        }
        var hz = CodProfile.PrimaryRefreshHz();
        var text = new System.Text.StringBuilder("What will change. Nothing has been written yet.\r\n\r\n");
        foreach (var file in files)
            text.AppendLine(CodProfile.Describe(file, CodProfile.Plan(CodProfile.Read(file), CodProfile.Chosen(false, hz))));
        text.AppendLine(CodProfile.LeftAlone(hz));
        text.AppendLine();
        text.AppendLine("Optional, asked when you press Apply: lowest visuals (render scale 50%, no bullet impacts).");
        _out.Text = text.ToString();
    }

    void Apply()
    {
        var files = CodProfile.Files();
        if (files.Length == 0) { Preview(); return; }
        if (CodProfile.GameRunning())
        {
            _out.Text = "The game is open. It rewrites this file when it closes and would undo our changes. Close the game, then press Apply again.";
            return;
        }
        var lowest = MessageBox.Show(this,
            "Also use the lowest-visuals options? Render scale 50% and no bullet impacts. More frames, a much softer picture.",
            "Lowest visuals", MessageBoxButtons.YesNo) == DialogResult.Yes;
        var hz = CodProfile.PrimaryRefreshHz();
        var report = new System.Text.StringBuilder();
        foreach (var file in files)
        {
            try
            {
                var plan = CodProfile.Plan(CodProfile.Read(file), CodProfile.Chosen(lowest, hz));
                if (plan.Changes.Count > 0) CodProfile.Write(file, plan.NewText);
                report.AppendLine(CodProfile.Describe(file, plan));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                report.AppendLine(file + "\r\n  Could not write: " + ex.Message);
            }
        }
        report.AppendLine("Done. Start the game. If something looks wrong, press Undo here.");
        report.AppendLine("If you use cloud save for the game config, it may bring the old values back.");
        _out.Text = report.ToString();
    }

    void Undo()
    {
        if (CodProfile.GameRunning())
        {
            _out.Text = "The game is open. Close it, then press Undo again.";
            return;
        }
        var restored = 0;
        foreach (var file in CodProfile.Files())
        {
            try { if (CodProfile.Restore(file)) restored++; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        _out.Text = restored == 0
            ? "There was no saved original to put back."
            : $"Put back the original file ({restored}). Everything is as it was before.";
    }
}
