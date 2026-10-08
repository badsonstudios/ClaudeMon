namespace ClaudeMon.Tests;

using ClaudeMon.Services;

public class CrashLoggingTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "ClaudeMonTests", Path.GetRandomFileName());

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    private string ReadLog(Logger logger) =>
        File.Exists(logger.FilePath) ? File.ReadAllText(logger.FilePath) : string.Empty;

    [Fact]
    public void LogUnhandled_WritesSourceTypeAndStack()
    {
        var logger = new Logger(_dir);
        Exception thrown;
        try
        {
            throw new InvalidOperationException("Collection was modified");
        }
        catch (InvalidOperationException ex)
        {
            thrown = ex;
        }

        CrashLogging.LogUnhandled(logger, thrown, "AppDomain.UnhandledException");

        var log = ReadLog(logger);
        Assert.Contains("[ERROR] FATAL (AppDomain.UnhandledException)", log);
        Assert.Contains(nameof(InvalidOperationException), log);
        Assert.Contains("Collection was modified", log);
        // Exception.ToString() carries the stack trace; the thrown-and-caught
        // exception above guarantees there is one to carry.
        Assert.Contains(nameof(LogUnhandled_WritesSourceTypeAndStack), log);
    }

    [Fact]
    public void LogUnhandled_NonExceptionObject_StillWritesALine()
    {
        var logger = new Logger(_dir);

        // AppDomain.UnhandledException's ExceptionObject is typed object; a non-Exception
        // payload arrives here as null and must still leave a trace.
        CrashLogging.LogUnhandled(logger, null, "AppDomain.UnhandledException");

        Assert.Contains("(no exception object)", ReadLog(logger));
    }

    [Fact]
    public void LogUnhandled_NeverThrows()
    {
        // A malformed directory path (the \0) makes Logger drop the line internally — its
        // catch covers the path-shape ArgumentException — and the crash hook's own catch-all
        // backstops that. This documents the never-throws contract of both layers.
        var logger = new Logger(Path.Combine(_dir, "sub\0invalid"));

        var exception = Record.Exception(() =>
            CrashLogging.LogUnhandled(logger, new InvalidOperationException("x"), "test"));

        Assert.Null(exception);
    }
}
