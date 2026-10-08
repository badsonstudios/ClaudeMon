namespace ClaudeMon.Services;

/// <summary>
/// Last-chance crash diagnostics (#206): writes unhandled exceptions from any thread to the
/// app log before the process dies. <c>Application.SetUnhandledExceptionMode</c> only covers
/// the UI message loop — an exception on a thread-pool or timer thread previously terminated
/// the process with nothing in <c>claudemon-*.log</c> (the only traces were the Windows Event
/// Log and WER). Best-effort: the handlers must never throw, and exception messages never
/// carry token contents (nothing in the app puts secrets in exception text).
/// </summary>
internal static class CrashLogging
{
    /// <summary>Hooks the process-wide unhandled-exception sources to <paramref name="logger"/>.</summary>
    public static void Register(Logger logger)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            LogUnhandled(logger, e.ExceptionObject as Exception, "AppDomain.UnhandledException");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            // Observed or not, an unobserved task exception doesn't kill a .NET Core process —
            // this hook exists purely so the fault is visible in the app's own log.
            e.SetObserved();
            LogUnhandled(logger, e.Exception, "TaskScheduler.UnobservedTaskException");
        };
        // Message-loop exceptions (CatchException mode) raise this instead of the two above.
        // Subscribing suppresses the built-in ThreadExceptionDialog, so it is re-shown here to
        // keep the existing Continue/Quit behaviour — the hook only adds the log line.
        Application.ThreadException += (_, e) =>
        {
            LogUnhandled(logger, e.Exception, "Application.ThreadException");
            try
            {
                using var dialog = new ThreadExceptionDialog(e.Exception);
                if (dialog.ShowDialog() == DialogResult.Abort)
                {
                    // Mirror the framework's Abort path (ApplicationExit + forms close),
                    // deviating only in the non-zero exit code.
                    Application.Exit();
                    Environment.Exit(1);
                }
            }
            catch
            {
                // The dialog leg failing (shutdown teardown, OOM) must not escape the
                // handler; the fault is already logged, so just finish dying.
                Environment.Exit(1);
            }
        };
    }

    // Internal seam for tests: the formatting and the never-throws contract.
    internal static void LogUnhandled(Logger logger, Exception? exception, string source)
    {
        try
        {
            logger.Error($"FATAL ({source}): {exception?.ToString() ?? "(no exception object)"}");
        }
        catch
        {
            // A crash logger that crashes helps no one — swallow everything.
        }
    }
}
