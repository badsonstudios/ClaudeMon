namespace ClaudeMon.Tests;

using ClaudeMon.Configuration;
using ClaudeMon.Models;

public class ConfigManagerTests : IDisposable
{
    private readonly string _tempDir;

    public ConfigManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"claudemon-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public void Load_NoConfigFile_CreatesDefaults()
    {
        var path = Path.Combine(_tempDir, "config.json");
        var manager = new ConfigManager(path);

        manager.Load();

        Assert.Equal(5, manager.Settings.PollIntervalMinutes);
        Assert.True(manager.Settings.AlertThresholds.PaceAlertsEnabled);
        Assert.Equal(PaceSensitivity.Balanced, manager.Settings.AlertThresholds.PaceSensitivity);
        Assert.Equal(90, manager.Settings.AlertThresholds.NearCapWarning);
        Assert.True(manager.Settings.Notifications.Enabled);
        Assert.True(File.Exists(path)); // Should have created the file
    }

    [Fact]
    public void Save_ThenLoad_RoundTrips()
    {
        var path = Path.Combine(_tempDir, "config.json");
        var manager = new ConfigManager(path);

        var settings = new AppSettings
        {
            PollIntervalMinutes = 3,
            AlertThresholds = new AlertThresholds
            {
                PaceAlertsEnabled = false,
                PaceSensitivity = PaceSensitivity.Late,
                NearCapWarning = 85,
                SevenDayWarning = 60,
            },
            Notifications = new NotificationSettings
            {
                Enabled = false,
                NotifyOnReset = true,
            },
        };

        manager.Update(settings);

        // Load into a new manager
        var manager2 = new ConfigManager(path);
        manager2.Load();

        Assert.Equal(3, manager2.Settings.PollIntervalMinutes);
        Assert.False(manager2.Settings.AlertThresholds.PaceAlertsEnabled);
        Assert.Equal(PaceSensitivity.Late, manager2.Settings.AlertThresholds.PaceSensitivity);
        Assert.Equal(85, manager2.Settings.AlertThresholds.NearCapWarning);
        Assert.Equal(60, manager2.Settings.AlertThresholds.SevenDayWarning);
        Assert.False(manager2.Settings.Notifications.Enabled);
        Assert.True(manager2.Settings.Notifications.NotifyOnReset);
    }

    [Fact]
    public void Load_CorruptedFile_UsesDefaults()
    {
        var path = Path.Combine(_tempDir, "config.json");
        File.WriteAllText(path, "this is not valid json {{{}}}");

        var manager = new ConfigManager(path);
        manager.Load();

        Assert.Equal(5, manager.Settings.PollIntervalMinutes);
    }

    [Fact]
    public void TaskbarDisplay_RoundTrips()
    {
        var path = Path.Combine(_tempDir, "config.json");
        var manager = new ConfigManager(path);

        manager.Update(new AppSettings
        {
            TaskbarDisplay = new TaskbarDisplaySettings
            {
                Enabled = true,
                ShowSevenDay = true,
                LabelColor = TaskbarTextColor.Black,
                NumberColor = TaskbarTextColor.DarkGray,
            },
        });

        var manager2 = new ConfigManager(path);
        manager2.Load();

        Assert.True(manager2.Settings.TaskbarDisplay.Enabled);
        Assert.True(manager2.Settings.TaskbarDisplay.ShowSevenDay);
        Assert.Equal(TaskbarTextColor.Black, manager2.Settings.TaskbarDisplay.LabelColor);
        Assert.Equal(TaskbarTextColor.DarkGray, manager2.Settings.TaskbarDisplay.NumberColor);
    }

    [Fact]
    public void TaskbarDisplay_DefaultColors_AreWhiteLabelAndAutoNumber()
    {
        var settings = new AppSettings();
        Assert.Equal(TaskbarTextColor.White, settings.TaskbarDisplay.LabelColor);
        Assert.Equal(TaskbarTextColor.Auto, settings.TaskbarDisplay.NumberColor);
    }

    [Fact]
    public void TaskbarDisplay_ShowSevenDay_DefaultsToFalse()
    {
        var settings = new AppSettings();
        Assert.False(settings.TaskbarDisplay.ShowSevenDay);
    }

    [Fact]
    public void TaskbarDisplay_AllMonitorsAndOffset_RoundTrip()
    {
        var path = Path.Combine(_tempDir, "config.json");
        var manager = new ConfigManager(path);

        // A negative offset confirms the signed int round-trips (the "negative = left" contract).
        manager.Update(new AppSettings
        {
            TaskbarDisplay = new TaskbarDisplaySettings
            {
                AllMonitors = true,
                HorizontalOffset = -40,
            },
        });

        var manager2 = new ConfigManager(path);
        manager2.Load();

        Assert.True(manager2.Settings.TaskbarDisplay.AllMonitors);
        Assert.Equal(-40, manager2.Settings.TaskbarDisplay.HorizontalOffset);
    }

    [Fact]
    public void TaskbarDisplay_AllMonitors_DefaultsToFalse()
    {
        // Load-bearing: defaulting to true would silently opt every existing user into
        // multi-monitor overlays on upgrade.
        var settings = new AppSettings();
        Assert.False(settings.TaskbarDisplay.AllMonitors);
    }

    [Fact]
    public void TaskbarDisplay_HorizontalOffset_DefaultsToZero()
    {
        var settings = new AppSettings();
        Assert.Equal(0, settings.TaskbarDisplay.HorizontalOffset);
    }

    [Fact]
    public void TaskbarDisplay_Size_DefaultsToStandard()
    {
        // Load-bearing: Standard maps to a 1.0 factor, so an upgrade (config with no "size"
        // key) must leave the taskbar rendering exactly as it was.
        var settings = new AppSettings();
        Assert.Equal(TaskbarSize.Standard, settings.TaskbarDisplay.Size);
        Assert.Equal(1f, settings.TaskbarDisplay.Size.Factor());
    }

    [Fact]
    public void TaskbarDisplay_Size_RoundTrips()
    {
        var path = Path.Combine(_tempDir, "config.json");
        var manager = new ConfigManager(path);

        manager.Update(new AppSettings
        {
            TaskbarDisplay = new TaskbarDisplaySettings { Size = TaskbarSize.ExtraLarge },
        });

        var manager2 = new ConfigManager(path);
        manager2.Load();

        Assert.Equal(TaskbarSize.ExtraLarge, manager2.Settings.TaskbarDisplay.Size);
    }

    [Theory]
    [InlineData(TaskbarSize.Small, 0.75f)]
    [InlineData(TaskbarSize.Standard, 1f)]
    [InlineData(TaskbarSize.Large, 1.25f)]
    [InlineData(TaskbarSize.ExtraLarge, 1.5f)]
    public void TaskbarSize_Factor_MatchesAdvertisedPercentages(TaskbarSize size, float expected)
    {
        Assert.Equal(expected, size.Factor());
    }

    [Fact]
    public void CheckForUpdates_DefaultsToTrue()
    {
        var settings = new AppSettings();
        Assert.True(settings.CheckForUpdates);
    }

    [Fact]
    public void UpdateSettings_RoundTrip()
    {
        var path = Path.Combine(_tempDir, "config.json");
        var manager = new ConfigManager(path);

        manager.Update(new AppSettings
        {
            CheckForUpdates = false,
            LastNotifiedVersion = "0.6.0",
        });

        var manager2 = new ConfigManager(path);
        manager2.Load();

        Assert.False(manager2.Settings.CheckForUpdates);
        Assert.Equal("0.6.0", manager2.Settings.LastNotifiedVersion);
    }

    [Fact]
    public void Save_CreatesDirectoryIfNeeded()
    {
        var path = Path.Combine(_tempDir, "subdir", "config.json");
        var manager = new ConfigManager(path);

        manager.Update(new AppSettings());

        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Save_IsAtomic_LeavesNoTempFileAndPreservesConfigOnRewrite()
    {
        var path = Path.Combine(_tempDir, "config.json");
        var manager = new ConfigManager(path);

        manager.Update(new AppSettings { PollIntervalMinutes = 7 });
        manager.Update(new AppSettings { PollIntervalMinutes = 9 }); // overwrite an existing file

        // The temp file used for the atomic write must be renamed away, not left behind.
        Assert.Empty(Directory.GetFiles(_tempDir, "*.tmp"));

        var reloaded = new ConfigManager(path);
        reloaded.Load();
        Assert.Equal(9, reloaded.Settings.PollIntervalMinutes);
    }

    [Fact]
    public void Save_OverwritesStaleTempFile()
    {
        var path = Path.Combine(_tempDir, "config.json");
        // A leftover temp from a previously interrupted write must not break the next save.
        File.WriteAllText(path + ".tmp", "stale garbage");

        var manager = new ConfigManager(path);
        manager.Update(new AppSettings { PollIntervalMinutes = 4 });

        Assert.Empty(Directory.GetFiles(_tempDir, "*.tmp"));
        var reloaded = new ConfigManager(path);
        reloaded.Load();
        Assert.Equal(4, reloaded.Settings.PollIntervalMinutes);
    }

    [Fact]
    public void PollInterval_ReturnsCorrectTimeSpan()
    {
        var settings = new AppSettings { PollIntervalMinutes = 3 };
        Assert.Equal(TimeSpan.FromMinutes(3), settings.PollInterval);
    }

    [Fact]
    public void DefaultSettings_HaveExpectedValues()
    {
        var settings = new AppSettings();
        Assert.Equal(5, settings.PollIntervalMinutes);
        Assert.True(settings.AlertThresholds.PaceAlertsEnabled);
        Assert.Equal(PaceSensitivity.Balanced, settings.AlertThresholds.PaceSensitivity);
        Assert.Equal(90, settings.AlertThresholds.NearCapWarning);
        Assert.Equal(50, settings.AlertThresholds.SevenDayWarning);
        Assert.True(settings.Notifications.Enabled);
        Assert.False(settings.Notifications.NotifyOnReset);
        Assert.True(settings.TaskbarDisplay.Enabled);
        Assert.Equal(1, settings.ConfigVersion);
    }
}
