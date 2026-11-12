namespace JesusKpekpes;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Any(a => string.Equals(a, "--uninstall", StringComparison.OrdinalIgnoreCase)))
        {
            Uninstall.Run();
            return;
        }
        Application.Run(args.Contains("--on") ? new GuideForm(true) : new HomeForm());
    }
}
