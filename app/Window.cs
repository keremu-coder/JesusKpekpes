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
