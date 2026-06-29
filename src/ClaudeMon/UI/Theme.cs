namespace ClaudeMon.UI;

using System.Drawing;

/// <summary>
/// The colour palette for the Settings window's custom-painted bits — the accents and controls the
/// app-wide colour mode doesn't theme (section headers, the <see cref="ToggleSwitch"/>, the spin
/// buttons, the dialog buttons). <see cref="Current"/> picks the light or dark variant from the
/// Windows app theme, so the dialog matches whatever Windows is set to.
/// </summary>
internal sealed class Theme
{
    public required bool IsDark { get; init; }

    public required Color HeaderAccent { get; init; }
    public required Color Divider { get; init; }
    public required Color HintText { get; init; }

    public required Color ButtonBack { get; init; }
    public required Color ButtonText { get; init; }
    public required Color ButtonBorder { get; init; }

    public required Color ToggleTrackOn { get; init; }
    public required Color ToggleTrackOff { get; init; }
    public required Color ToggleTrackDisabled { get; init; }
    public required Color ToggleKnob { get; init; }
    public required Color ToggleKnobDisabled { get; init; }

    public required Color FieldBack { get; init; }
    public required Color FieldText { get; init; }
    public required Color SpinButtonBack { get; init; }
    public required Color SpinArrow { get; init; }
    public required Color SpinSeparator { get; init; }

    // Pinned once at startup (see Initialize) so it stays consistent with the app-wide colour mode,
    // which is also fixed at startup. A Windows theme change is picked up on the next launch.
    private static Theme? _current;

    /// <summary>Pins the palette to match the resolved startup theme. Called once from Program.</summary>
    public static void Initialize(bool dark) => _current = dark ? Dark : Light;

    /// <summary>
    /// The palette matching the Windows theme (resolved at startup). <see cref="Initialize"/> must
    /// run first — re-reading the registry here could pick a different answer than the app-wide
    /// colour mode was pinned to and half-theme the dialog, so this requires the explicit pin.
    /// </summary>
    public static Theme Current => _current
        ?? throw new InvalidOperationException("Theme.Initialize must be called before Theme.Current is read.");

    private static readonly Theme Dark = new()
    {
        IsDark = true,
        HeaderAccent = Color.FromArgb(72, 150, 235), // standard blue, lifted for dark
        Divider = Color.FromArgb(70, 70, 70),
        HintText = Color.FromArgb(150, 150, 150),
        ButtonBack = Color.FromArgb(55, 55, 55),
        ButtonText = Color.FromArgb(230, 230, 230),
        ButtonBorder = Color.FromArgb(95, 95, 95),
        ToggleTrackOn = Color.FromArgb(72, 150, 235), // standard blue
        ToggleTrackOff = Color.FromArgb(85, 85, 85),
        ToggleTrackDisabled = Color.FromArgb(55, 55, 55),
        ToggleKnob = Color.FromArgb(245, 245, 245),
        ToggleKnobDisabled = Color.FromArgb(120, 120, 120),
        FieldBack = Color.FromArgb(45, 45, 45),
        FieldText = Color.FromArgb(225, 225, 225),
        SpinButtonBack = Color.FromArgb(62, 62, 62),
        SpinArrow = Color.FromArgb(210, 210, 210),
        SpinSeparator = Color.FromArgb(80, 80, 80),
    };

    private static readonly Theme Light = new()
    {
        IsDark = false,
        HeaderAccent = Color.FromArgb(0, 120, 212), // standard Windows blue
        Divider = Color.FromArgb(222, 222, 222),
        HintText = Color.FromArgb(120, 120, 120),
        ButtonBack = Color.FromArgb(246, 246, 246),
        ButtonText = Color.FromArgb(30, 30, 30),
        ButtonBorder = Color.FromArgb(180, 180, 180),
        ToggleTrackOn = Color.FromArgb(0, 120, 212), // standard Windows blue
        ToggleTrackOff = Color.FromArgb(198, 198, 198),
        ToggleTrackDisabled = Color.FromArgb(226, 226, 226),
        ToggleKnob = Color.FromArgb(255, 255, 255),
        ToggleKnobDisabled = Color.FromArgb(238, 238, 238),
        FieldBack = Color.FromArgb(252, 252, 252),
        FieldText = Color.FromArgb(20, 20, 20),
        SpinButtonBack = Color.FromArgb(238, 238, 238),
        SpinArrow = Color.FromArgb(90, 90, 90),
        SpinSeparator = Color.FromArgb(208, 208, 208),
    };
}
