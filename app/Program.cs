namespace JesusKpekpes;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.Run(args.Contains("--on") ? new GuideForm(true) : new HomeForm());
    }
}
