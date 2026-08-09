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
    // failed to survive the round trip can't coincide with the default.
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
}
