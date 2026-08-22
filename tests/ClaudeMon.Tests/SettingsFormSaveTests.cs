namespace ClaudeMon.Tests;

using ClaudeMon.Configuration;
using ClaudeMon.Models;
using ClaudeMon.UI;

/// <summary>
/// What OK writes back (#143). The dialog only surfaces some of each settings record, so the save
/// layers the controls onto the saved settings with <c>with</c> — a reconstruction would reset
/// every field the dialog doesn't edit on every save. These drive the real form's
/// <see cref="SettingsForm.BuildSettings"/>, which is the half of the OK handler that doesn't touch
/// the registry.
/// </summary>
public class SettingsFormSaveTests : IDisposable
{
    private readonly string _tempDir;

    public SettingsFormSaveTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"claudemon-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        // Program pins the palette at startup from the Windows theme; the form's constructor reads
        // it. Nothing here depends on which variant, so pin the light one.
        Theme.Initialize(dark: false);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    // Every taskbar field the dialog does edit, all away from their defaults, so a value that
    // failed to survive the round trip can't coincide with the default. CycleHome stays at its
    // default because it is derived from the toggles + style rather than being a control of its
    // own (see BuildSettings) — under Bar these toggles show one bar, so home is null; the two
    // tests below own that derivation.
    private static readonly TaskbarDisplaySettings EditedFields = new()
    {
        Enabled = false,
        Style = TaskbarStyle.Bar,
        BarWidth = TaskbarBarWidth.Wide,
        SizePercent = 125,
        ShowSessionUsage = false,
        ShowWeeklyUsage = true,
        ShowTimeToLimit = true,
        ShowTimeToReset = true,
        ShowPercentSign = true,
        LabelColor = TaskbarTextColor.DarkGray,
        NumberColor = TaskbarTextColor.MatchTaskbar,
        AllMonitors = true,
        HorizontalOffset = -20,
        PrimaryHorizontalOffset = 12,
    };

    private ConfigManager ManagerHolding(AppSettings settings)
    {
        var manager = new ConfigManager(Path.Combine(_tempDir, "config.json"));
        manager.Update(settings);
        return manager;
    }

    [Fact]
    public void BuildSettings_RoundTripsTheDriftSettings()
    {
        // The drift alert settings (issue #186) map both ways, including the off/non-default case.
        var manager = ManagerHolding(new AppSettings
        {
            AlertThresholds = new AlertThresholds
            {
                DriftAlertsEnabled = false,
                DriftThresholdPercent = 35,
            },
        });
        using var form = new SettingsForm(manager);

        var thresholds = form.BuildSettings().AlertThresholds;
        Assert.False(thresholds.DriftAlertsEnabled);
        Assert.Equal(35, thresholds.DriftThresholdPercent);
    }

    [Fact]
    public void BuildSettings_RoundTripsThePlan_IncludingNotSet()
    {
        // The plan combo (issue #184) maps both ways: a saved plan loads into the combo and
        // saves back unchanged, and "Not set" is a real value that round-trips as null.
        using (var form = new SettingsForm(ManagerHolding(new AppSettings { Plan = ClaudePlan.Max5x })))
            Assert.Equal(ClaudePlan.Max5x, form.BuildSettings().Plan);

        using (var form = new SettingsForm(ManagerHolding(new AppSettings())))
            Assert.Null(form.BuildSettings().Plan);
    }

    [Fact]
    public void BuildSettings_PreservesATaskbarFieldTheDialogDoesNotEdit()
    {
        // LegacyShowSevenDay is the only such field today (ConfigManager's 0.10.x migration owns
        // it), but the point is the shape: anything added to TaskbarDisplaySettings without a
        // control has to survive a save rather than silently snapping back to its default.
        var manager = ManagerHolding(new AppSettings
        {
            TaskbarDisplay = new TaskbarDisplaySettings { LegacyShowSevenDay = true },
        });
        using var form = new SettingsForm(manager);

        Assert.True(form.BuildSettings().TaskbarDisplay.LegacyShowSevenDay);
    }

    [Fact]
    public void BuildSettings_RoundTripsEveryTaskbarFieldTheDialogEdits()
    {
        var manager = ManagerHolding(new AppSettings { TaskbarDisplay = EditedFields });
        using var form = new SettingsForm(manager);

        // Untouched controls save exactly what they were loaded with.
        Assert.Equal(EditedFields, form.BuildSettings().TaskbarDisplay);
    }

    [Fact]
    public void BuildSettings_TakesTheEditedFieldsFromTheControlsNotTheSavedSettings()
    {
        var manager = ManagerHolding(new AppSettings { TaskbarDisplay = EditedFields });
        using var form = new SettingsForm(manager);

        // Settings change under the open dialog (click-to-cycle does exactly this), so "saved" and
        // "on screen" now disagree: the dialog's values must still win for the fields it owns, and
        // only the fields it doesn't own may come from the current settings.
        manager.Update(manager.Settings with
        {
            TaskbarDisplay = new TaskbarDisplaySettings { LegacyShowSevenDay = true },
        });

        Assert.Equal(EditedFields with { LegacyShowSevenDay = true }, form.BuildSettings().TaskbarDisplay);
    }

    // #156's click-to-cycle home. The dialog has no control of its own for it — it is derived
    // from the display toggles, and only when those actually change. The saved toggles are what
    // the dialog loaded, so re-saving them changes nothing; a test makes the two disagree by
    // moving the saved ones under the open form, which is exactly the state a real edit produces.
    private static readonly TaskbarMetricSelection Composition = new(
        Session: true, Weekly: false, TimeToLimit: false, TimeToReset: true);

    [Fact]
    public void BuildSettings_KeepsTheCycleHomeWhenTheDisplayTogglesAreUntouched()
    {
        // Mid-cycle (the toggles hold the collapsed readout, not anything the user chose), open
        // Settings to change the poll interval and click OK. Recomputing home from those toggles
        // would destroy the layout the next wrap was about to restore — #156 all over again, one
        // unrelated dialog visit removed.
        var manager = ManagerHolding(new AppSettings
        {
            TaskbarDisplay = new TaskbarDisplaySettings
            {
                ShowSessionUsage = false,
                ShowWeeklyUsage = true,
                CycleHome = Composition,
            },
        });
        using var form = new SettingsForm(manager);

        Assert.Equal(Composition, form.BuildSettings().TaskbarDisplay.CycleHome);
    }

    [Fact]
    public void BuildSettings_MakesEditedDisplayTogglesTheNewCycleHome()
    {
        // Editing the toggles to a composition does replace the remembered one: Settings is the
        // source of truth, so a home from a layout you have since edited can't come back.
        var manager = ManagerHolding(new AppSettings
        {
            TaskbarDisplay = new TaskbarDisplaySettings
            {
                ShowSessionUsage = true,
                ShowWeeklyUsage = true,
                CycleHome = Composition,
            },
        });
        using var form = new SettingsForm(manager);

        // The dialog is holding session + weekly; make the saved toggles disagree, as an edit does.
        manager.Update(manager.Settings with
        {
            TaskbarDisplay = manager.Settings.TaskbarDisplay.WithMetrics(Composition),
        });

        Assert.Equal(
            new TaskbarMetricSelection(
                Session: true, Weekly: true, TimeToLimit: false, TimeToReset: false),
            form.BuildSettings().TaskbarDisplay.CycleHome);
    }

    [Fact]
    public void BuildSettings_ForgetsTheCycleHomeWhenTheTogglesAreEditedToASingleMetric()
    {
        // Nothing left to protect: the ring reaches a single-metric readout on its own, so a
        // remembered home would only be a second stop showing the same thing.
        var manager = ManagerHolding(new AppSettings
        {
            TaskbarDisplay = new TaskbarDisplaySettings { CycleHome = Composition },
        });
        using var form = new SettingsForm(manager);

        // The dialog is holding the default session-only readout; make the saved toggles disagree.
        manager.Update(manager.Settings with
        {
            TaskbarDisplay = manager.Settings.TaskbarDisplay.WithMetrics(Composition),
        });

        Assert.Null(form.BuildSettings().TaskbarDisplay.CycleHome);
    }

    // #155: AlertThresholds and Budgets are saved the same layered way. Neither has a field
    // without a control today — unlike TaskbarDisplay, which has LegacyShowSevenDay to test with —
    // so the field the save has to protect is the *next* one somebody adds. These stand in for it:
    // `with` copies the record it was given, extra state and runtime type included, where a
    // reconstruction hands back a fresh default-valued instance of the base type.
    private sealed record AlertThresholdsWithUnsurfacedField : AlertThresholds
    {
        public bool KeptState { get; init; }
    }

    private sealed record BudgetSettingsWithUnsurfacedField : BudgetSettings
    {
        public bool KeptState { get; init; }
    }

    [Fact]
    public void BuildSettings_PreservesAnAlertThresholdFieldTheDialogDoesNotEdit()
    {
        var manager = ManagerHolding(new AppSettings
        {
            AlertThresholds = new AlertThresholdsWithUnsurfacedField { KeptState = true },
        });
        using var form = new SettingsForm(manager);

        var saved = Assert.IsType<AlertThresholdsWithUnsurfacedField>(form.BuildSettings().AlertThresholds);
        Assert.True(saved.KeptState);
    }

    [Fact]
    public void BuildSettings_PreservesABudgetFieldTheDialogDoesNotEdit()
    {
        var manager = ManagerHolding(new AppSettings
        {
            Budgets = new BudgetSettingsWithUnsurfacedField { KeptState = true },
        });
        using var form = new SettingsForm(manager);

        var saved = Assert.IsType<BudgetSettingsWithUnsurfacedField>(form.BuildSettings().Budgets);
        Assert.True(saved.KeptState);
    }

    // Every alert/budget field the dialog does edit, all away from their defaults and inside the
    // controls' ranges (50–100, 10–100, 1–10000), so a value that failed to round-trip can't
    // coincide with the default.
    private static readonly AlertThresholds EditedAlerts = new()
    {
        PaceAlertsEnabled = false,
        PaceSensitivity = PaceSensitivity.Late,
        NearCapWarning = 75,
        SevenDayWarning = 42,
    };

    private static readonly BudgetSettings EditedBudgets = new()
    {
        DailyEnabled = true,
        DailyCapUsd = 12.5,
        WeeklyEnabled = true,
        WeeklyCapUsd = 99.25,
    };

    [Fact]
    public void BuildSettings_RoundTripsEveryAlertThresholdFieldTheDialogEdits()
    {
        var manager = ManagerHolding(new AppSettings { AlertThresholds = EditedAlerts });
        using var form = new SettingsForm(manager);

        // Untouched controls save exactly what they were loaded with — layering onto the saved
        // record must not change a field the dialog owns.
        Assert.Equal(EditedAlerts, form.BuildSettings().AlertThresholds);
    }

    [Fact]
    public void BuildSettings_RoundTripsEveryBudgetFieldTheDialogEdits()
    {
        var manager = ManagerHolding(new AppSettings { Budgets = EditedBudgets });
        using var form = new SettingsForm(manager);

        Assert.Equal(EditedBudgets, form.BuildSettings().Budgets);
    }
}
