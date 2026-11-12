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
        using var gate = new Mutex(true, @"Local\JesusKpekpes", out var first);
        if (!first) return;
        Quiet.ReplaceOptimizer();
        Application.Run(new HomeForm());
        GC.KeepAlive(gate);
    }
}
