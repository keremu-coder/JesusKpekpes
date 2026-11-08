namespace JesusKpekpes;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new GuideForm(args.Contains("--on")));
    }
}
