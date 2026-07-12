namespace ClaudeMon.Tests;

using ClaudeMon.Services;

public class LoggerTests : IDisposable
{
    private readonly string _dir;

    // Fake clock so tests can cross midnight without waiting for one.
    private DateTime _now = DateTime.Now;

    public LoggerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"claudemon-log-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, true);
    }

    private Logger CreateLogger(string? directory = null, long maxBytes = 1_000_000) =>
        new(directory ?? _dir, maxBytes, () => _now);

    private string PathForDate(DateTime date) =>
        Path.Combine(_dir, $"claudemon-{date:yyyy-MM-dd}.log");

    private string TodayPath => PathForDate(_now.Date);

    [Fact]
    public void Write_CreatesDailyFileWithLevelAndMessage()
    {
        var logger = CreateLogger();

        logger.Info("hello world");
        logger.Warn("careful now");
        logger.Error("it broke");

        Assert.True(File.Exists(TodayPath));
        var text = File.ReadAllText(TodayPath);
        Assert.Contains("[INFO] hello world", text);
        Assert.Contains("[WARN] careful now", text);
        Assert.Contains("[ERROR] it broke", text);
    }

    [Fact]
    public void Write_CreatesMissingDirectory()
    {
        var nested = Path.Combine(_dir, "logs");
        Assert.False(Directory.Exists(nested));

        var logger = CreateLogger(nested);
        logger.Info("first line");

        Assert.True(File.Exists(Path.Combine(nested, $"claudemon-{_now:yyyy-MM-dd}.log")));
    }

    [Fact]
    public void Write_CollapsesMultilineMessageToOneLine()
    {
        var logger = CreateLogger();

        logger.Info("line one\nline two\r\nline three");

        var lines = File.ReadAllLines(TodayPath);
        Assert.Single(lines);
        Assert.Contains("line one line two", lines[0]);
    }

    [Fact]
    public void FilePath_And_DirectoryPath_AreExposed()
    {
        var logger = CreateLogger();

        Assert.Equal(TodayPath, logger.FilePath);
        Assert.Equal(_dir, logger.DirectoryPath);
    }

    [Fact]
    public void FilePath_RollsOver_WhenDateChanges()
    {
        var logger = CreateLogger();
        logger.Info("before midnight");
        var yesterdayPath = TodayPath;

        _now = _now.AddDays(1);
        logger.Info("after midnight");

        Assert.Equal(TodayPath, logger.FilePath);
        Assert.NotEqual(yesterdayPath, logger.FilePath);
        Assert.True(File.Exists(yesterdayPath), "previous day's file should remain");
        Assert.Contains("after midnight", File.ReadAllText(TodayPath));
    }

    [Fact]
    public void Write_RotatesAtCap_AndBoundsTotalSize()
    {
        const long cap = 2_000; // tiny cap to force rotation quickly
        var logger = CreateLogger(maxBytes: cap);

        // Each line is ~60 bytes; 200 lines (~12 KB) far exceeds the cap.
        for (var i = 0; i < 200; i++)
            logger.Info($"entry number {i} with some padding text to add bytes");

        var backup = TodayPath + ".1";
        Assert.True(File.Exists(TodayPath), "current log should exist");
        Assert.True(File.Exists(backup), "a rotated backup should have been created");

        // Current file was reset at rotation, so it stays near the cap (cap + one line),
        // and the total on disk is bounded to roughly 2x the cap.
        var currentSize = new FileInfo(TodayPath).Length;
        var totalSize = currentSize + new FileInfo(backup).Length;
        Assert.True(currentSize <= cap * 2, $"current size {currentSize} not bounded");
        Assert.True(totalSize <= cap * 3, $"total size {totalSize} not bounded");
    }

    [Fact]
    public void Write_IsThreadSafe_AllLinesLandWithoutThrowing()
    {
        var logger = CreateLogger();
        const int threads = 8;
        const int perThread = 50;

        Parallel.For(0, threads, t =>
        {
            for (var i = 0; i < perThread; i++)
                logger.Info($"t{t}-i{i}");
        });

        var lineCount = File.ReadAllLines(TodayPath).Length;
        Assert.Equal(threads * perThread, lineCount);
    }

    [Fact]
    public void Write_NeverThrows_OnIoFailure()
    {
        // Point the logger at a directory path whose parent is a *file*, so directory
        // creation and writes fail — logging must swallow it rather than throw.
        var fileAsParent = Path.Combine(_dir, "blocker");
        File.WriteAllText(fileAsParent, "x");
        var doomed = CreateLogger(Path.Combine(fileAsParent, "nested"));

        var ex = Record.Exception(() => doomed.Info("should not throw"));
        Assert.Null(ex);
    }

    [Fact]
    public void Write_DeletesFilesPastRetention_AndKeepsRecentOnes()
    {
        var oldFile = CreateBackdatedFile($"claudemon-{_now.AddDays(-9):yyyy-MM-dd}.log", ageDays: 9);
        var oldBackup = CreateBackdatedFile($"claudemon-{_now.AddDays(-9):yyyy-MM-dd}.log.1", ageDays: 9);
        var recentFile = CreateBackdatedFile($"claudemon-{_now.AddDays(-2):yyyy-MM-dd}.log", ageDays: 2);

        CreateLogger().Info("triggers startup cleanup");

        Assert.False(File.Exists(oldFile), "file past retention should be deleted");
        Assert.False(File.Exists(oldBackup), "rotation backup past retention should be deleted");
        Assert.True(File.Exists(recentFile), "file within retention should be kept");
        Assert.True(File.Exists(TodayPath));
    }

    [Fact]
    public void Write_DeletesLegacySingleFileLogs_PastRetention()
    {
        var legacy = CreateBackdatedFile("claudemon.log", ageDays: 9);
        var legacyBackup = CreateBackdatedFile("claudemon.log.1", ageDays: 9);

        CreateLogger().Info("triggers startup cleanup");

        Assert.False(File.Exists(legacy), "legacy claudemon.log should be deleted");
        Assert.False(File.Exists(legacyBackup), "legacy claudemon.log.1 should be deleted");
    }

    [Fact]
    public void Write_RunsCleanupOnRollover_NotOnEveryWrite()
    {
        var logger = CreateLogger();
        logger.Info("first write runs startup cleanup");

        // A file that ages past retention mid-day is untouched by ordinary writes...
        var oldFile = CreateBackdatedFile("claudemon-stale.log", ageDays: 9);
        logger.Info("same-day write");
        Assert.True(File.Exists(oldFile), "cleanup should not run on every write");

        // ...but the next date rollover sweeps it.
        _now = _now.AddDays(1);
        logger.Info("rollover write");
        Assert.False(File.Exists(oldFile), "rollover should trigger cleanup");
    }

    [Fact]
    public void Write_SwallowsCleanupFailure_AndKeepsLogging()
    {
        var locked = CreateBackdatedFile("claudemon-locked.log", ageDays: 9);
        var deletable = CreateBackdatedFile("claudemon-gone.log", ageDays: 9);

        // Hold the file open with no sharing so File.Delete throws.
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var logger = CreateLogger();
            var ex = Record.Exception(() => logger.Info("cleanup failure must not throw"));

            Assert.Null(ex);
            Assert.True(File.Exists(locked), "locked file could not be deleted");
            Assert.False(File.Exists(deletable), "other old files should still be deleted");
            Assert.Contains("cleanup failure must not throw", File.ReadAllText(TodayPath));
        }
    }

    [Fact]
    public void LatestExistingFilePath_PrefersTodaysFile()
    {
        var logger = CreateLogger();
        CreateBackdatedFile($"claudemon-{_now.AddDays(-1):yyyy-MM-dd}.log", ageDays: 1);
        logger.Info("today has entries");

        Assert.Equal(TodayPath, logger.LatestExistingFilePath);
    }

    [Fact]
    public void LatestExistingFilePath_FallsBackToNewestFile_WhenNothingLoggedToday()
    {
        var logger = CreateLogger();
        CreateBackdatedFile($"claudemon-{_now.AddDays(-3):yyyy-MM-dd}.log", ageDays: 3);
        var newest = CreateBackdatedFile($"claudemon-{_now.AddDays(-1):yyyy-MM-dd}.log", ageDays: 1);
        // A rotation backup must not win even if touched more recently.
        CreateBackdatedFile($"claudemon-{_now.AddDays(-1):yyyy-MM-dd}.log.1", ageDays: 0);

        Assert.Equal(newest, logger.LatestExistingFilePath);
    }

    [Fact]
    public void LatestExistingFilePath_IsNull_WhenNoLogsExist()
    {
        var logger = CreateLogger(Path.Combine(_dir, "empty"));

        Assert.Null(logger.LatestExistingFilePath);
    }

    private string CreateBackdatedFile(string name, int ageDays)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "old content");
        File.SetLastWriteTime(path, _now.AddDays(-ageDays));
        return path;
    }
}
