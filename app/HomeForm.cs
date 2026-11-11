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
        var audio = Button("Audio tune", 150, () => new GuideForm(false).Show());
        var pc = Button("Optimize the PC", 214, () => new PcForm().Show());
        var programs = Button("Get the audio programs", 278, () => new ProgramsForm().ShowDialog(this));
        var cod = Button("Call of Duty settings", 342, () => new CodForm().Show());
        var check = Button("Sound check (compare two tunes)", 406, () => new SoundCheckForm().Show());
        var bench = Button("Monitor and frames", 470, () => new BenchForm().Show());
        var startup = Button("Startup menu", 534, () => new StartupForm().Show());
        Controls.AddRange([title, body, audio, pc, programs, cod, check, bench, startup]);
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

    public ProgramsForm()
    {
        Text = "Audio programs";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(520, 460);
        BackColor = Color.FromArgb(7, 9, 15);
        ForeColor = Color.FromArgb(242, 243, 245);
        Font = new Font("Segoe UI", 10f);
        _status.SetBounds(20, 16, 480, 70);
        _status.ForeColor = Color.FromArgb(180, 184, 198);
        Controls.Add(_status);
        var y = 96;
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
                    Text = "Download + open",
                    Bounds = new Rectangle(360, y + 32, 140, 30),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Color.FromArgb(40, 120, 80),
                    ForeColor = Color.White,
                };
                get.FlatAppearance.BorderSize = 0;
                get.Click += async (_, _) =>
                {
                    get.Enabled = false;
                    _status.Text = "Downloading " + row.Name + " and checking it...";
                    var message = await Installer.Fetch(row.Name);
                    _status.Text = message;
                    get.Enabled = true;
                };
                Controls.Add(get);
            }
            y += 72;
        }
        var again = new Button
        {
            Text = "Check again",
            Bounds = new Rectangle(20, 400, 160, 40),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(88, 101, 242),
            ForeColor = Color.White,
        };
        again.FlatAppearance.BorderSize = 0;
        again.Click += (_, _) => RefreshStatus();
        Controls.Add(again);
        RefreshStatus();
    }

    void RefreshStatus()
    {
        var lines = new System.Text.StringBuilder();
        lines.AppendLine("Install what is missing, then press Check again. Download + open checks the file first, then opens the vendor setup for you to click through.");
        var labels = Controls.OfType<Label>().Where(l => l != _status).ToList();
        foreach (var label in labels) Controls.Remove(label);
        var y = 96;
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
        _status.Text = lines.ToString();
    }
}
