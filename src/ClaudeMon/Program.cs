namespace ClaudeMon;

using ClaudeMon.UI;

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

        // Match the app to the Windows "mode" (the dark/light toggle that drives the taskbar). We
        // resolve it once here and pin both the experimental colour mode (which themes the window
        // chrome + standard controls) and our palette to it, so the Settings window is fully and
        // consistently themed. Forced light uses Classic — the experimental "System" mode follows
        // the separate apps theme and dark-styles the default button, which we don't want. A
        // Windows theme change is picked up on the next launch. Must be set before any window.
        var dark = !SystemTheme.IsLightWindowsMode();
        Theme.Initialize(dark);
#pragma warning disable WFO5001
        Application.SetColorMode(dark ? SystemColorMode.Dark : SystemColorMode.Classic);
#pragma warning restore WFO5001

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        using var app = new TrayApplication();
        Application.Run();
    }
}
