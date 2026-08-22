namespace ClaudeMon.Tests;

using System.Text.Json;
using ClaudeMon.Configuration;
using ClaudeMon.Models;

public class ConfigManagerTests : IDisposable
{
    /// <summary>
    /// Discarding log sink for the failure-path tests. Without it they'd fall through to the
    /// default sink and append to the developer's real ClaudeMon log file.
    /// </summary>
    private static readonly Action<string> NoLog = _ => { };

    /// <summary>A log sink that is itself broken — diagnostics must not become a new throw path.</summary>
    private static readonly Action<string> ThrowingLog =
        _ => throw new InvalidOperationException("log is broken");

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
    public void Budgets_RoundTrip_AndDefaultOffOnUpgrade()
    {
        var path = Path.Combine(_tempDir, "config.json");
        var manager = new ConfigManager(path);
        manager.Load();

        // Absent keys (an upgraded config) → budgets exist but are off.
        Assert.False(manager.Settings.Budgets.DailyEnabled);
        Assert.False(manager.Settings.Budgets.WeeklyEnabled);
        Assert.Null(manager.Settings.BudgetAlertState);

        manager.Update(manager.Settings with
        {
            Budgets = new BudgetSettings
            {
                DailyEnabled = true,
                DailyCapUsd = 12.5,
                WeeklyEnabled = true,
                WeeklyCapUsd = 62.75,
            },
            BudgetAlertState = new BudgetAlertState("2026-07-19", 80, "2026-07-13", 50),
        });

        var reloaded = new ConfigManager(path);
        reloaded.Load();

        Assert.True(reloaded.Settings.Budgets.DailyEnabled);
        Assert.Equal(12.5, reloaded.Settings.Budgets.DailyCapUsd);
        Assert.Equal(62.75, reloaded.Settings.Budgets.WeeklyCapUsd);
        Assert.Equal(new BudgetAlertState("2026-07-19", 80, "2026-07-13", 50),
            reloaded.Settings.BudgetAlertState);
    }

    [Fact]
    public void Plan_RoundTripsAsAString_AndDefaultsToUnset()
    {
        var path = Path.Combine(_tempDir, "config.json");
        var manager = new ConfigManager(path);
        manager.Load();

        // Absent key (an upgraded or fresh config) → unset; unset stays an absent key on save.
        Assert.Null(manager.Settings.Plan);
        Assert.DoesNotContain("claudePlan", File.ReadAllText(path));

        manager.Update(manager.Settings with { Plan = ClaudePlan.Max20x });

        // Stored by name, not ordinal — a reordered enum can't silently change the plan.
        Assert.Contains("\"claudePlan\": \"Max20x\"", File.ReadAllText(path));
        var reloaded = new ConfigManager(path);
        reloaded.Load();
        Assert.Equal(ClaudePlan.Max20x, reloaded.Settings.Plan);
    }

    [Fact]
    public void ServiceIncidentLevel_RoundTrips_AndIsAbsentByDefault()
    {
        var path = Path.Combine(_tempDir, "config.json");
        var manager = new ConfigManager(path);
        manager.Load();

        // Nothing latched on a fresh (or upgraded) config — the first incident must alert.
        Assert.Null(manager.Settings.ServiceIncidentLevel);

        manager.Update(manager.Settings with { ServiceIncidentLevel = ServiceStatusLevel.Major });

        // Written by name, so a hand-read config stays legible and reordering the enum can't
        // silently reinterpret a saved latch.
        Assert.Contains("\"Major\"", File.ReadAllText(path));

        var reloaded = new ConfigManager(path);
        reloaded.Load();

        Assert.Equal(ServiceStatusLevel.Major, reloaded.Settings.ServiceIncidentLevel);

        reloaded.Update(reloaded.Settings with { ServiceIncidentLevel = null });

        var cleared = new ConfigManager(path);
        cleared.Load();

        Assert.Null(cleared.Settings.ServiceIncidentLevel);
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

        var manager = new ConfigManager(path, NoLog);
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
        Assert.False(settings.TaskbarDisplay.ShowTimeToLimit);
        Assert.False(settings.TaskbarDisplay.ShowTimeToReset);
        Assert.False(settings.TaskbarDisplay.ShowPercentSign);
        Assert.Equal(TaskbarMetricSelection.SessionOnly, settings.TaskbarDisplay.Metrics);
    }

    [Fact]
    public void TaskbarDisplay_Metrics_MirrorTheToggles_BothWays()
    {
        // The click-to-cycle gesture (#71) writes the same toggles Settings edits, via these
        // two projections — if they ever drift, cycling and Settings would disagree.
        var display = new TaskbarDisplaySettings
        {
            ShowSessionUsage = false,
            ShowWeeklyUsage = true,
            ShowTimeToLimit = true,
            ShowTimeToReset = false,
        };
        Assert.Equal(new TaskbarMetricSelection(false, true, true, false), display.Metrics);

        var cycled = display.WithMetrics(TaskbarMetricSelection.For(TaskbarMetric.TimeToReset));
        Assert.False(cycled.ShowSessionUsage);
        Assert.False(cycled.ShowWeeklyUsage);
        Assert.False(cycled.ShowTimeToLimit);
        Assert.True(cycled.ShowTimeToReset);

        // Everything else on the record survives the cycle — it edits the metrics only.
        Assert.Equal(display with
        {
            ShowSessionUsage = false,
            ShowWeeklyUsage = false,
            ShowTimeToLimit = false,
            ShowTimeToReset = true,
        }, cycled);
    }

    [Fact]
    public void TaskbarDisplay_CycledMetric_SurvivesARestart()
    {
        // The acceptance criterion behind "the cycled choice persists": cycling writes
        // settings through the same ConfigManager save Settings uses.
        var path = Path.Combine(_tempDir, "config.json");
        var manager = new ConfigManager(path);
        var cycled = new AppSettings().TaskbarDisplay
            .WithMetrics(TaskbarMetricSelection.For(TaskbarMetric.TimeToLimit));
        manager.Update(new AppSettings { TaskbarDisplay = cycled });

        var manager2 = new ConfigManager(path);
        manager2.Load();

        Assert.Equal(TaskbarMetricSelection.For(TaskbarMetric.TimeToLimit),
            manager2.Settings.TaskbarDisplay.Metrics);

        // Metrics is a derived view of the toggles, so it must not be serialized: a stored
        // copy would be get-only dead weight that silently disagrees after a hand edit.
        Assert.DoesNotContain("metrics", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TaskbarDisplay_CycleHome_SurvivesARestartMidCycle()
    {
        // #156: the composition click-to-cycle wraps back to is remembered state, not a derived
        // view, so it has to survive a restart in the middle of a cycle — otherwise quitting on
        // the "weekly" stop still loses the layout the wrap was going to restore.
        var path = Path.Combine(_tempDir, "config.json");
        var manager = new ConfigManager(path);
        var composed = new TaskbarMetricSelection(
            Session: true, Weekly: false, TimeToLimit: false, TimeToReset: true);

        manager.Update(new AppSettings
        {
            TaskbarDisplay = new AppSettings().TaskbarDisplay
                .WithMetrics(TaskbarMetricSelection.For(TaskbarMetric.Weekly)) with
            {
                CycleHome = composed,
            },
        });

        var manager2 = new ConfigManager(path);
        manager2.Load();

        Assert.Equal(composed, manager2.Settings.TaskbarDisplay.CycleHome);
        Assert.Equal(
            TaskbarMetricSelection.For(TaskbarMetric.Weekly), manager2.Settings.TaskbarDisplay.Metrics);
    }

    [Fact]
    public void TaskbarDisplay_CycleHome_IsAbsentUntilSomethingIsRemembered()
    {
        // Nothing to restore on a fresh or upgraded config, and the key stays out of the file
        // rather than writing an all-false composition that would mean something different.
        Assert.Null(new AppSettings().TaskbarDisplay.CycleHome);

        var path = Path.Combine(_tempDir, "config.json");
        var manager = new ConfigManager(path);
        manager.Load();

        Assert.Null(manager.Settings.TaskbarDisplay.CycleHome);
        Assert.DoesNotContain("cycleHome", File.ReadAllText(path));
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
                ShowTimeToLimit = true,
                ShowTimeToReset = true,
                ShowPercentSign = true,
            },
        });

        var manager2 = new ConfigManager(path);
        manager2.Load();

        Assert.False(manager2.Settings.TaskbarDisplay.ShowSessionUsage);
        Assert.True(manager2.Settings.TaskbarDisplay.ShowWeeklyUsage);
        Assert.True(manager2.Settings.TaskbarDisplay.ShowTimeToLimit);
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
    public void Save_DirectoryUncreatable_DoesNotThrowAndKeepsSettingsInMemory()
    {
        // Creating the settings directory can fail for the same reasons the write can (ACL denial,
        // a folder lock, disk full). Simulated here by parking a *file* where the directory needs
        // to go, which makes Directory.CreateDirectory throw. Save is best-effort: the in-memory
        // settings still serve this session and the next Save retries.
        var blocker = Path.Combine(_tempDir, "blocked");
        File.WriteAllText(blocker, "not a directory");
        var path = Path.Combine(blocker, "config.json");
        var manager = new ConfigManager(path, NoLog);

        manager.Update(new AppSettings { PollIntervalMinutes = 13 });

        Assert.Equal(13, manager.Settings.PollIntervalMinutes);
        Assert.False(File.Exists(path)); // nothing was written
        Assert.Empty(Directory.GetFiles(_tempDir, "*.tmp")); // and no temp was orphaned
    }

    [Fact]
    public void Load_DirectoryUncreatable_DoesNotThrowAndFallsBackToDefaults()
    {
        // Load() saves a fresh config when none exists, so the same unwritable directory reaches
        // Save() through the startup path — the one place a throw would take the app down.
        var blocker = Path.Combine(_tempDir, "blocked");
        File.WriteAllText(blocker, "not a directory");
        var manager = new ConfigManager(Path.Combine(blocker, "config.json"), NoLog);

        manager.Load();

        Assert.Equal(new AppSettings().PollIntervalMinutes, manager.Settings.PollIntervalMinutes);
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
    public void Save_TempFileUnwritable_DoesNotThrowAndKeepsSettingsInMemory()
    {
        // A transient lock on the temp file (AV scanner, an earlier write still being flushed)
        // must not crash the app: the in-memory settings still serve this session and the next
        // Save retries. The lock also blocks the best-effort cleanup, which is equally silent.
        var path = Path.Combine(_tempDir, "config.json");
        var manager = new ConfigManager(path, NoLog);
        using var blocker = new FileStream(
            path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None);

        manager.Update(new AppSettings { PollIntervalMinutes = 11 });

        Assert.Equal(11, manager.Settings.PollIntervalMinutes);
        Assert.False(File.Exists(path)); // nothing was swapped into place
    }

    [Fact]
    public void Save_ExistingConfigLocked_DoesNotThrowAndCleansUpTheTemp()
    {
        // The temp write succeeds but the atomic swap can't take the destination. The previous
        // config survives untouched and the orphaned temp is cleaned up.
        var path = Path.Combine(_tempDir, "config.json");
        var manager = new ConfigManager(path, NoLog);
        manager.Update(new AppSettings { PollIntervalMinutes = 6 });

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            manager.Update(new AppSettings { PollIntervalMinutes = 8 });
        }

        Assert.Equal(8, manager.Settings.PollIntervalMinutes);
        Assert.Empty(Directory.GetFiles(_tempDir, "*.tmp"));

        var reloaded = new ConfigManager(path);
        reloaded.Load();
        Assert.Equal(6, reloaded.Settings.PollIntervalMinutes); // the on-disk file was preserved
    }

    [Fact]
    public void Load_CorruptedFile_LogsThePathAndTheException()
    {
        // "My settings reset themselves" is undiagnosable if the parse failure leaves no trace.
        var path = Path.Combine(_tempDir, "config.json");
        const string garbage = "this is not valid json {{{}}}";
        File.WriteAllText(path, garbage);
        var logs = new List<string>();

        new ConfigManager(path, logs.Add).Load();

        var entry = Assert.Single(logs);
        Assert.Contains(path, entry);
        // The real parser text, not a generic "load failed" — reproduced here so the assertion
        // doesn't hard-code a framework message that could be reworded or localized.
        var expected = Assert.ThrowsAny<Exception>(
            () => { JsonSerializer.Deserialize<AppSettings>(garbage); }).Message;
        Assert.Contains(expected, entry);
    }

    [Fact]
    public void Save_DirectoryUncreatable_LogsThePathAndTheException()
    {
        var blocker = Path.Combine(_tempDir, "blocked");
        File.WriteAllText(blocker, "not a directory");
        var path = Path.Combine(blocker, "config.json");
        var logs = new List<string>();

        new ConfigManager(path, logs.Add).Update(new AppSettings());

        var entry = Assert.Single(logs);
        Assert.Contains(path, entry);
        var expected = Assert.ThrowsAny<Exception>(
            () => { Directory.CreateDirectory(blocker); }).Message;
        Assert.Contains(expected, entry);
    }

    [Fact]
    public void Save_TempFileUnwritable_LogsThePathAndTheException()
    {
        var path = Path.Combine(_tempDir, "config.json");
        var logs = new List<string>();
        using var blocker = new FileStream(
            path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None);

        new ConfigManager(path, logs.Add).Update(new AppSettings());

        AssertSaveFailureLogged(Assert.Single(logs), path);
    }

    [Fact]
    public void Save_ExistingConfigLocked_LogsThePathAndTheException()
    {
        // The swap-into-place failure, as opposed to the write failure above.
        var path = Path.Combine(_tempDir, "config.json");
        var logs = new List<string>();
        var manager = new ConfigManager(path, logs.Add);
        manager.Update(new AppSettings());

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            manager.Update(new AppSettings { PollIntervalMinutes = 8 });
        }

        AssertSaveFailureLogged(Assert.Single(logs), path);
    }

    // The Win32 text behind a locked file is localized, so assert the shape instead: our own
    // prefix, then whatever the OS called the failure.
    private static void AssertSaveFailureLogged(string entry, string path)
    {
        var prefix = $"Could not save settings to {path}: ";
        Assert.StartsWith(prefix, entry);
        Assert.NotEmpty(entry[prefix.Length..]);
    }

    [Fact]
    public void LoadAndSave_Succeeding_LogNothing()
    {
        // Successful persistence must stay silent — a settings file written every few minutes
        // would otherwise bury the diagnostics this log exists for.
        var path = Path.Combine(_tempDir, "config.json");
        var logs = new List<string>();

        var manager = new ConfigManager(path, logs.Add);
        manager.Load();                                              // creates the file
        manager.Update(new AppSettings { PollIntervalMinutes = 9 }); // overwrites it
        new ConfigManager(path, logs.Add).Load();                    // reads it back

        Assert.Empty(logs);
    }

    [Fact]
    public void Load_LogSinkThrows_StillFallsBackToDefaults()
    {
        // Logging is diagnostics for a best-effort path; it must never become the thing that
        // finally throws out of Load.
        var path = Path.Combine(_tempDir, "config.json");
        File.WriteAllText(path, "this is not valid json {{{}}}");
        var manager = new ConfigManager(path, ThrowingLog);

        manager.Load();

        Assert.Equal(new AppSettings().PollIntervalMinutes, manager.Settings.PollIntervalMinutes);
    }

    [Fact]
    public void Save_LogSinkThrows_StillKeepsSettingsInMemory()
    {
        var blocker = Path.Combine(_tempDir, "blocked");
        File.WriteAllText(blocker, "not a directory");
        var manager = new ConfigManager(Path.Combine(blocker, "config.json"), ThrowingLog);

        manager.Update(new AppSettings { PollIntervalMinutes = 13 });

        Assert.Equal(13, manager.Settings.PollIntervalMinutes);
    }

    [Fact]
    public void DefaultConfigPath_IsUnderLocalAppData_AndResolvesWithoutTouchingDisk()
    {
        // Constructing with no path must only resolve a location — the real user config is not
        // read or written here (this test would otherwise stomp the developer's own settings).
        var manager = new ConfigManager();

        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClaudeMon",
            "config.json");
        Assert.Equal(expected, manager.ConfigPath);
        Assert.Equal(new AppSettings().PollIntervalMinutes, manager.Settings.PollIntervalMinutes);
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
