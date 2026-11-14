namespace JesusKpekpes;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // Used by the updater before it swaps files: the new program must start and end cleanly. No window, no lock.
        if (args.Length > 0 && string.Equals(args[0], "--selftest", StringComparison.OrdinalIgnoreCase)) return 0;
        // Run by the NEW program from the update folder: wait for the old window, swap the files, open the installed one.
        if (args.Length == 6 && string.Equals(args[0], "--finish-update", StringComparison.OrdinalIgnoreCase) && int.TryParse(args[1], out var pid))
        {
            Updater.Finish(pid, args[2], args[3], args[4], args[5]);
            return 0;
        }
        ApplicationConfiguration.Initialize();
        if (args.Any(a => string.Equals(a, "--uninstall", StringComparison.OrdinalIgnoreCase)))
        {
            Uninstall.Run();
            return 0;
        }
        using var gate = new Mutex(true, @"Local\JesusKpekpes", out var first);
        if (!first) return 0;
        Quiet.ReplaceOptimizer();
        try { ScriptHost.Run("-Action GameBarQuiet -Silent", 20000); } catch (Exception) { }
        Application.Run(new HomeForm());
        GC.KeepAlive(gate);
        return 0;
    }
}
