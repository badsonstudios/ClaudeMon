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
        Assert.Equal(50, manager.Settings.AlertThresholds.FiveHourWarning);
        Assert.Equal(80, manager.Settings.AlertThresholds.FiveHourCritical);
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
                FiveHourWarning = 70,
                FiveHourCritical = 90,
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
        Assert.Equal(70, manager2.Settings.AlertThresholds.FiveHourWarning);
        Assert.Equal(90, manager2.Settings.AlertThresholds.FiveHourCritical);
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
        Assert.Equal(50, settings.AlertThresholds.FiveHourWarning);
        Assert.Equal(80, settings.AlertThresholds.FiveHourCritical);
        Assert.Equal(50, settings.AlertThresholds.SevenDayWarning);
        Assert.True(settings.Notifications.Enabled);
        Assert.False(settings.Notifications.NotifyOnReset);
        Assert.True(settings.TaskbarDisplay.Enabled);
        Assert.Equal(1, settings.ConfigVersion);
    }
}
