namespace ClaudeMon;

using ClaudeMon.Services;
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

        // Before SetColorMode: the exception mode can only be changed while no controls exist
        // on the thread, and dark-mode initialization can create one (#206) — the previous
        // order (SetColorMode first) was a latent startup crash.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        // Last-chance diagnostics (#206): CatchException above only covers the UI message
        // loop, so a crash on any other thread must be logged here or it leaves no trace in
        // the app's own log. Registered before any background work can start.
        var logger = new Logger();
        CrashLogging.Register(logger);

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

        using var app = new TrayApplication(logger);
        Application.Run();
    }
}
