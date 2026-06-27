namespace ClaudeMon.Tests;

using ClaudeMon.Services;

public class LoggerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _logPath;

    public LoggerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"claudemon-log-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _logPath = Path.Combine(_tempDir, "logs", "claudemon.log");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public void Write_CreatesFileWithLevelAndMessage()
    {
        var logger = new Logger(_logPath);

        logger.Info("hello world");
        logger.Warn("careful now");
        logger.Error("it broke");

        Assert.True(File.Exists(_logPath));
        var text = File.ReadAllText(_logPath);
        Assert.Contains("[INFO] hello world", text);
        Assert.Contains("[WARN] careful now", text);
        Assert.Contains("[ERROR] it broke", text);
    }

    [Fact]
    public void Write_CreatesMissingDirectory()
    {
        // Parent "logs" directory does not exist yet.
        Assert.False(Directory.Exists(Path.GetDirectoryName(_logPath)));

        var logger = new Logger(_logPath);
        logger.Info("first line");

        Assert.True(File.Exists(_logPath));
    }

    [Fact]
    public void Write_CollapsesMultilineMessageToOneLine()
    {
        var logger = new Logger(_logPath);

        logger.Info("line one\nline two\r\nline three");

        var lines = File.ReadAllLines(_logPath);
        Assert.Single(lines);
        Assert.Contains("line one line two", lines[0]);
    }

    [Fact]
    public void FilePath_And_DirectoryPath_AreExposed()
    {
        var logger = new Logger(_logPath);

        Assert.Equal(_logPath, logger.FilePath);
        Assert.Equal(Path.GetDirectoryName(_logPath), logger.DirectoryPath);
    }

    [Fact]
    public void Write_RotatesAtCap_AndBoundsTotalSize()
    {
        const long cap = 2_000; // tiny cap to force rotation quickly
        var logger = new Logger(_logPath, maxBytes: cap);

        // Each line is ~60 bytes; 200 lines (~12 KB) far exceeds the cap.
        for (var i = 0; i < 200; i++)
            logger.Info($"entry number {i} with some padding text to add bytes");

        var backup = _logPath + ".1";
        Assert.True(File.Exists(_logPath), "current log should exist");
        Assert.True(File.Exists(backup), "a rotated backup should have been created");

        // Current file was reset at rotation, so it stays near the cap (cap + one line),
        // and the total on disk is bounded to roughly 2x the cap.
        var currentSize = new FileInfo(_logPath).Length;
        var totalSize = currentSize + new FileInfo(backup).Length;
        Assert.True(currentSize <= cap * 2, $"current size {currentSize} not bounded");
        Assert.True(totalSize <= cap * 3, $"total size {totalSize} not bounded");
    }

    [Fact]
    public void Write_IsThreadSafe_AllLinesLandWithoutThrowing()
    {
        var logger = new Logger(_logPath);
        const int threads = 8;
        const int perThread = 50;

        Parallel.For(0, threads, t =>
        {
            for (var i = 0; i < perThread; i++)
                logger.Info($"t{t}-i{i}");
        });

        var lineCount = File.ReadAllLines(_logPath).Length;
        Assert.Equal(threads * perThread, lineCount);
    }

    [Fact]
    public void Write_NeverThrows_OnIoFailure()
    {
        // Point the logger at a path whose parent is a *file*, so directory creation
        // and writes fail — logging must swallow it rather than throw.
        var fileAsParent = Path.Combine(_tempDir, "blocker");
        File.WriteAllText(fileAsParent, "x");
        var doomed = new Logger(Path.Combine(fileAsParent, "nested", "claudemon.log"));

        var ex = Record.Exception(() => doomed.Info("should not throw"));
        Assert.Null(ex);
    }
}
