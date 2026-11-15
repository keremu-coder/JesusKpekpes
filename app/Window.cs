using System.Runtime.CompilerServices;

namespace JesusKpekpes;

static class Window
{
    public static readonly Icon Mark = Load();

    public static void Prepare(Form form)
    {
        form.Load += (_, _) => Apply(form);
    }

    public static void Apply(Form form)
    {
        form.Icon = Mark;
        form.ShowInTaskbar = false;
        form.MaximizeBox = false;
        form.FormBorderStyle = FormBorderStyle.FixedSingle;
        form.MinimizeBox = form is HomeForm;
        Look.Dress(form);
    }

    static Icon Load()
    {
        try
        {
            var icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            if (icon is not null) return icon;
        }
        catch (Exception)
        {
            /* the plain icon is the fallback */
        }
        return SystemIcons.Application;
    }
}

/// Navy #14213D, cream #F4EDE0, coral #FF6B5B.
/// The window follows the Windows app theme, light or dark. Same controls either way.
/// Painted once, on show. No timer and no animation, so an idle window costs nothing.
static class Look
{
    public static readonly Color Cream = Color.FromArgb(244, 237, 224);
    public static readonly Color Coral = Color.FromArgb(255, 107, 91);

    static readonly bool Light = ReadLight();
    static readonly Color Ground;
    static readonly Color Raised;
    static readonly Color Face;
    static readonly Color FaceHot;
    static readonly Color FaceDown;
    static readonly Color FaceDim;
    static readonly Color Ink;
    static readonly Color InkSoft;
    static readonly Color InkDim;
    static readonly Color Hair;
    static readonly Color Sage;
    static readonly Color MenuSel;
    static readonly Color MenuPress;
    static readonly SolidBrush CoralBrush = new(Coral);

    static Look()
    {
        if (Light)
        {
            Ground = Color.FromArgb(244, 237, 224);
            Raised = Color.FromArgb(255, 252, 247);
            Face = Color.FromArgb(20, 33, 61);
            FaceHot = Color.FromArgb(36, 52, 90);
            FaceDown = Color.FromArgb(12, 22, 44);
            FaceDim = Color.FromArgb(214, 206, 192);
            Ink = Color.FromArgb(20, 33, 61);
            InkSoft = Color.FromArgb(74, 84, 108);
            InkDim = Color.FromArgb(130, 122, 110);
            Hair = Color.FromArgb(214, 204, 188);
            Sage = Color.FromArgb(30, 110, 70);
            MenuSel = Color.FromArgb(232, 220, 204);
            MenuPress = Color.FromArgb(220, 208, 190);
        }
        else
        {
            Ground = Color.FromArgb(20, 33, 61);
            Raised = Color.FromArgb(30, 46, 80);
            Face = Color.FromArgb(42, 60, 98);
            FaceHot = Color.FromArgb(54, 74, 116);
            FaceDown = Color.FromArgb(16, 26, 50);
            FaceDim = Color.FromArgb(16, 24, 44);
            Ink = Cream;
            InkSoft = Color.FromArgb(196, 186, 168);
            InkDim = Color.FromArgb(132, 124, 112);
            Hair = Color.FromArgb(62, 80, 112);
            Sage = Color.FromArgb(168, 196, 154);
            MenuSel = Face;
            MenuPress = FaceDown;
        }
    }

    static bool ReadLight()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int theme && theme != 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    static readonly ConditionalWeakTable<Button, ButtonState> Buttons = new();
    static readonly ConditionalWeakTable<Control, object> Seen = new();
    static readonly object SeenMark = new();

    sealed class ButtonState
    {
        public bool Mark;
        public bool Quiet;
        public bool Wired;
    }

    public static void Dress(Control root)
    {
        if (root is Form form)
        {
            typeof(Control).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?.SetValue(form, true);
            form.BackColor = Ground;
            form.ForeColor = Ink;
            if (form is HomeForm)
            {
                form.Paint += (_, e) =>
                {
                    var title = form.Controls.OfType<Label>().FirstOrDefault(l => l.Font.Size >= 18f);
                    if (title is null) return;
                    e.Graphics.FillRectangle(CoralBrush, title.Left, title.Bottom + 4, 36, 2);
                };
            }
        }
        Walk(root);
    }

    public static void Menu(ContextMenuStrip menu)
    {
        menu.Renderer = new ToolStripProfessionalRenderer(new MenuColors()) { RoundedEdges = false };
        menu.BackColor = Light ? Ground : Raised;
        menu.ForeColor = Ink;
        foreach (ToolStripItem item in menu.Items)
            item.ForeColor = Ink;
    }

    static void Walk(Control root)
    {
        if (!Seen.TryAdd(root, SeenMark)) return;
        root.ControlAdded += (_, e) =>
        {
            if (e.Control is null) return;
            Paint(e.Control);
            Walk(e.Control);
        };
        Paint(root);
        foreach (Control child in root.Controls) Walk(child);
    }

    static void Paint(Control control)
    {
        switch (control)
        {
            case Button button:
                DressButton(button);
                break;
            case Label label:
                label.ForeColor = LabelInk(label.ForeColor, label.Font.Size >= 18f);
                label.BackColor = Color.Transparent;
                break;
            case CheckBox box:
                box.FlatStyle = FlatStyle.Flat;
                box.BackColor = Ground;
                box.ForeColor = Ink;
                box.FlatAppearance.BorderColor = Hair;
                box.FlatAppearance.CheckedBackColor = Raised;
                box.FlatAppearance.MouseOverBackColor = Light ? MenuSel : Face;
                box.FlatAppearance.MouseDownBackColor = Light ? MenuPress : FaceDown;
                break;
            case TextBoxBase text:
                text.BackColor = Raised;
                text.ForeColor = Ink;
                break;
            case ListBox list:
                list.BackColor = Raised;
                list.ForeColor = Ink;
                break;
            case ComboBox combo:
                combo.BackColor = Raised;
                combo.ForeColor = Ink;
                break;
            case TrackBar bar:
                bar.BackColor = Ground;
                break;
            case ProgressBar progress:
                progress.BackColor = Raised;
                progress.ForeColor = Coral;
                break;
        }
    }

    static Color LabelInk(Color ink, bool title)
    {
        if (title) return Ink;
        if (ink.B > 200 && ink.B > ink.R + 40 && ink.B > ink.G + 40) return Coral;
        if (ink.G > ink.R + 40 && ink.G > 160) return Sage;
        if (ink.R > 150 && ink.G > 150 && ink.B > 160 && Math.Abs(ink.R - ink.B) < 40) return InkSoft;
        if (ink.R > 220 && ink.G > 210) return Ink;
        return ink.A == 0 ? Ink : ink;
    }

    static void DressButton(Button button)
    {
        var state = Buttons.GetOrCreateValue(button);
        if (!state.Wired)
        {
            var ink = button.BackColor;
            state.Mark = (ink.G > ink.R + 30 && ink.G > 90 && ink.B < ink.G)
                || (ink.B > 180 && ink.R > 100 && ink.B > ink.G);
            state.Quiet = ink.R < 70 && ink.G < 80 && ink.B < 100 && ink.B + 5 >= ink.R;
            state.Wired = true;
            button.Cursor = Cursors.Hand;
            button.MouseEnter += (_, _) => button.FlatAppearance.BorderColor = Coral;
            button.MouseLeave += (_, _) => Edge(button);
            button.GotFocus += (_, _) => button.FlatAppearance.BorderColor = Coral;
            button.LostFocus += (_, _) => Edge(button);
            button.EnabledChanged += (_, _) => ApplyButton(button);
        }
        ApplyButton(button);
    }

    static void ApplyButton(Button button)
    {
        if (!Buttons.TryGetValue(button, out var state)) return;
        button.UseVisualStyleBackColor = false;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 1;
        button.BackColor = !button.Enabled ? FaceDim : state.Quiet ? Raised : Face;
        button.ForeColor = !button.Enabled ? InkDim : state.Quiet ? Ink : Cream;
        button.FlatAppearance.MouseOverBackColor = FaceHot;
        button.FlatAppearance.MouseDownBackColor = FaceDown;
        var wide = button.Width >= 160;
        button.Padding = new Padding(wide ? 8 : 4, 0, wide ? 8 : 4, 0);
        Edge(button);
    }

    static void Edge(Button button)
    {
        if (!Buttons.TryGetValue(button, out var state)) return;
        var coral = state.Mark || button.Focused;
        button.FlatAppearance.BorderColor = button.Enabled && coral ? Coral : Hair;
    }

    sealed class MenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Light ? Ground : Raised;
        public override Color MenuBorder => Hair;
        public override Color MenuItemBorder => Coral;
        public override Color MenuItemSelected => MenuSel;
        public override Color ImageMarginGradientBegin => Light ? Ground : Raised;
        public override Color ImageMarginGradientMiddle => Light ? Ground : Raised;
        public override Color ImageMarginGradientEnd => Light ? Ground : Raised;
        public override Color MenuItemSelectedGradientBegin => MenuSel;
        public override Color MenuItemSelectedGradientEnd => MenuSel;
        public override Color MenuItemPressedGradientBegin => MenuPress;
        public override Color MenuItemPressedGradientEnd => MenuPress;
    }
}

static class Quiet
{
    public static void ReplaceOptimizer()
    {
        try
        {
            var bundled = Path.Combine(AppContext.BaseDirectory, "pc", "controller", "KpekpesPCOptimizer.exe");
            var bin = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "KpekpesCODCustom", "controller", "bin");
            var installed = Path.Combine(bin, "KpekpesPCOptimizer.exe");
            if (!File.Exists(bundled) || !File.Exists(installed)) return;
            if (Same(bundled, installed)) return;
            foreach (var process in System.Diagnostics.Process.GetProcessesByName("KpekpesPCOptimizer"))
            {
                try
                {
                    process.Kill(true);
                    process.WaitForExit(4000);
                }
                catch (Exception)
                {
                    /* already gone */
                }
            }
            File.Copy(bundled, installed, true);
            var vbs = Path.Combine(bin, "run_hidden.vbs");
            if (!File.Exists(vbs)) return;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("wscript.exe", "\"" + vbs + "\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
            });
        }
        catch (Exception)
        {
            /* leave the old copy; the next Optimize replaces it */
        }
    }

    static bool Same(string a, string b)
    {
        using var left = File.OpenRead(a);
        using var right = File.OpenRead(b);
        var ha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(left));
        var hb = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(right));
        return ha == hb;
    }
}

