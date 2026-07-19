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
                ShowWeeklyUsage = true,
                LabelColor = TaskbarTextColor.Black,
                NumberColor = TaskbarTextColor.DarkGray,
            },
        });

        var manager2 = new ConfigManager(path);
        manager2.Load();

        Assert.True(manager2.Settings.TaskbarDisplay.Enabled);
        Assert.True(manager2.Settings.TaskbarDisplay.ShowWeeklyUsage);
        Assert.Equal(TaskbarTextColor.Black, manager2.Settings.TaskbarDisplay.LabelColor);
        Assert.Equal(TaskbarTextColor.DarkGray, manager2.Settings.TaskbarDisplay.NumberColor);
    }

    [Fact]
    public void TaskbarDisplay_MatchTaskbarColors_RoundTrip()
    {
        var path = Path.Combine(_tempDir, "config.json");
        var manager = new ConfigManager(path);

        manager.Update(new AppSettings
        {
            TaskbarDisplay = new TaskbarDisplaySettings
            {
                LabelColor = TaskbarTextColor.MatchTaskbar,
                NumberColor = TaskbarTextColor.MatchTaskbar,
            },
        });

        var manager2 = new ConfigManager(path);
        manager2.Load();

        Assert.Equal(TaskbarTextColor.MatchTaskbar, manager2.Settings.TaskbarDisplay.LabelColor);
        Assert.Equal(TaskbarTextColor.MatchTaskbar, manager2.Settings.TaskbarDisplay.NumberColor);
    }

    [Fact]
    public void TaskbarDisplay_DefaultColors_AreWhiteLabelAndAutoNumber()
    {
        var settings = new AppSettings();
        Assert.Equal(TaskbarTextColor.White, settings.TaskbarDisplay.LabelColor);
        Assert.Equal(TaskbarTextColor.Auto, settings.TaskbarDisplay.NumberColor);
    }

    [Fact]
    public void TaskbarDisplay_DisplayToggles_DefaultToSessionOnly()
    {
        // Load-bearing: session-on / weekly-off / countdown-off reproduces the original
        // 5-hour-only readout, so fresh installs and upgrades look unchanged.
        var settings = new AppSettings();
        Assert.True(settings.TaskbarDisplay.ShowSessionUsage);
        Assert.False(settings.TaskbarDisplay.ShowWeeklyUsage);
        Assert.False(settings.TaskbarDisplay.ShowTimeToReset);
        Assert.False(settings.TaskbarDisplay.ShowPercentSign);
    }

    [Fact]
    public void TaskbarDisplay_DisplayToggles_RoundTrip()
    {
        var path = Path.Combine(_tempDir, "config.json");
        var manager = new ConfigManager(path);

        // Weekly + countdown with session off — a combination the old toggle couldn't express.
        manager.Update(new AppSettings
        {
            TaskbarDisplay = new TaskbarDisplaySettings
            {
                ShowSessionUsage = false,
                ShowWeeklyUsage = true,
                ShowTimeToReset = true,
                ShowPercentSign = true,
            },
        });

        var manager2 = new ConfigManager(path);
        manager2.Load();

        Assert.False(manager2.Settings.TaskbarDisplay.ShowSessionUsage);
        Assert.True(manager2.Settings.TaskbarDisplay.ShowWeeklyUsage);
        Assert.True(manager2.Settings.TaskbarDisplay.ShowTimeToReset);
        Assert.True(manager2.Settings.TaskbarDisplay.ShowPercentSign);
    }

    [Fact]
    public void Notifications_SnoozeUntil_RoundTrips_AndDefaultsNull()
    {
        Assert.Null(new AppSettings().Notifications.SnoozeUntil);

        var path = Path.Combine(_tempDir, "config.json");
        var manager = new ConfigManager(path);
        var until = new DateTimeOffset(2026, 7, 19, 3, 30, 0, TimeSpan.Zero);

        manager.Update(new AppSettings
        {
            Notifications = new NotificationSettings { SnoozeUntil = until },
        });

        var manager2 = new ConfigManager(path);
        manager2.Load();

        Assert.Equal(until, manager2.Settings.Notifications.SnoozeUntil);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public void Load_LegacyShowSevenDay_MigratesToWeeklyToggle(string legacyValue, bool expectedWeekly)
    {
        // A 0.10.x config (raw JSON, not our serializer) with the pre-toggles key.
        var path = Path.Combine(_tempDir, "config.json");
        File.WriteAllText(path, $$"""{ "taskbarDisplay": { "showSevenDay": {{legacyValue}} } }""");

        var manager = new ConfigManager(path);
        manager.Load();

        Assert.Equal(expectedWeekly, manager.Settings.TaskbarDisplay.ShowWeeklyUsage);
        Assert.True(manager.Settings.TaskbarDisplay.ShowSessionUsage); // unchanged default
        Assert.Null(manager.Settings.TaskbarDisplay.LegacyShowSevenDay); // cleared by migration
    }

    [Fact]
    public void Load_LegacyTrueWithExplicitWeeklyFalse_LegacyWins()
    {
        // Both keys present (e.g. a downgrade-then-upgrade wrote showWeeklyUsage:false while the
        // old showSevenDay:true survived). Deliberate choice: the legacy opt-in wins, so a user
        // who had the 7-day readout keeps it.
        var path = Path.Combine(_tempDir, "config.json");
        File.WriteAllText(path,
            """{ "taskbarDisplay": { "showSevenDay": true, "showWeeklyUsage": false } }""");

        var manager = new ConfigManager(path);
        manager.Load();

        Assert.True(manager.Settings.TaskbarDisplay.ShowWeeklyUsage);
    }

    [Fact]
    public void Save_AfterLegacyMigration_DropsTheOldKey()
    {
        var path = Path.Combine(_tempDir, "config.json");
        File.WriteAllText(path, """{ "taskbarDisplay": { "showSevenDay": true } }""");

        var manager = new ConfigManager(path);
        manager.Load();
        manager.Update(manager.Settings); // any save after migration

        var written = File.ReadAllText(path);
        Assert.DoesNotContain("showSevenDay", written);
        Assert.Contains("showWeeklyUsage", written);
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
    public void TaskbarDisplay_PrimaryHorizontalOffset_RoundTrips_IndependentlyOfSecondary()
    {
        var path = Path.Combine(_tempDir, "config.json");
        var manager = new ConfigManager(path);

        // Distinct signed values confirm the two nudges persist independently.
        manager.Update(new AppSettings
        {
            TaskbarDisplay = new TaskbarDisplaySettings
            {
                PrimaryHorizontalOffset = -24,
                HorizontalOffset = 16,
            },
        });

        var manager2 = new ConfigManager(path);
        manager2.Load();

        Assert.Equal(-24, manager2.Settings.TaskbarDisplay.PrimaryHorizontalOffset);
        Assert.Equal(16, manager2.Settings.TaskbarDisplay.HorizontalOffset);
    }

    [Fact]
    public void TaskbarDisplay_PrimaryHorizontalOffset_DefaultsToZero()
    {
        // Load-bearing: 0 keeps the primary readout exactly tray-anchored, so an upgrade
        // (config with no "primaryHorizontalOffset" key) is visually unchanged.
        var settings = new AppSettings();
        Assert.Equal(0, settings.TaskbarDisplay.PrimaryHorizontalOffset);
    }

    [Fact]
    public void TaskbarDisplay_SizePercent_DefaultsTo100()
    {
        // Load-bearing: 100% is exactly the DPI-only scale, so an upgrade (config with no
        // "sizePercent" key) must leave the taskbar rendering exactly as it was.
        var settings = new AppSettings();
        Assert.Equal(100, settings.TaskbarDisplay.SizePercent);
    }

    [Fact]
    public void TaskbarDisplay_SizePercent_RoundTrips()
    {
        var path = Path.Combine(_tempDir, "config.json");
        var manager = new ConfigManager(path);

        // A value between the old dropdown's fixed steps — the whole point of the numeric field.
        manager.Update(new AppSettings
        {
            TaskbarDisplay = new TaskbarDisplaySettings { SizePercent = 60 },
        });

        var manager2 = new ConfigManager(path);
        manager2.Load();

        Assert.Equal(60, manager2.Settings.TaskbarDisplay.SizePercent);
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
            AutoInstallUpdates = true,
            IgnoredUpdateVersion = "0.6.0",
            PendingUpdateVersion = "0.7.0",
        });

        var manager2 = new ConfigManager(path);
        manager2.Load();

        Assert.False(manager2.Settings.CheckForUpdates);
        Assert.True(manager2.Settings.AutoInstallUpdates);
        Assert.Equal("0.6.0", manager2.Settings.IgnoredUpdateVersion);
        Assert.Equal("0.7.0", manager2.Settings.PendingUpdateVersion);
    }

    [Fact]
    public void AutoInstallUpdates_DefaultsToFalse()
    {
        // Load-bearing: on by default would silently start restarting existing users' apps
        // to install updates they never opted into.
        var settings = new AppSettings();
        Assert.False(settings.AutoInstallUpdates);
    }

    [Fact]
    public void Load_DropsLegacyLastNotifiedVersion()
    {
        // Pre-0.12 configs tracked "ballooned once per version" in lastNotifiedVersion. That
        // semantic ("was told") doesn't map to the new one ("chose to skip"), so the old key
        // must load harmlessly — ignored, not migrated — and disappear on the next save.
        var path = Path.Combine(_tempDir, "config.json");
        File.WriteAllText(path, """{"checkForUpdates":true,"lastNotifiedVersion":"0.6.0"}""");

        var manager = new ConfigManager(path);
        manager.Load();

        Assert.Null(manager.Settings.IgnoredUpdateVersion);

        manager.Save();
        Assert.DoesNotContain("lastNotifiedVersion", File.ReadAllText(path));
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

    [Theory]
    [InlineData(1)]  // saved by a version that still offered "1 minute"
    [InlineData(0)]  // hand-edited config
    public void PollInterval_FlooredAtTwoMinutes(int minutes)
    {
        // Polling every minute made the API refresh fail every other request, so the
        // effective interval never drops below 2 even if the persisted value does.
        var settings = new AppSettings { PollIntervalMinutes = minutes };
        Assert.Equal(TimeSpan.FromMinutes(2), settings.PollInterval);
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
