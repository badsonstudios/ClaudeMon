namespace ClaudeMon;

static class Program
{
    private const string MutexName = "Global\\ClaudeMon_SingleInstance";

    [STAThread]
    static void Main()
    {
        using var mutex = new Mutex(true, MutexName, out var createdNew);
        if (!createdNew)
        {
            // Another instance is already running
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        using var app = new TrayApplication();
        Application.Run();
    }
}
