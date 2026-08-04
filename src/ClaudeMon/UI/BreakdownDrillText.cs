namespace ClaudeMon.UI;

/// <summary>
/// The section headings above the two "Usage &amp; costs" tables (#112). Each table is either
/// showing everything ("By model") or the counterpart of a row selected in the other one
/// ("Projects using claude-fable-5"), and the heading is the only thing on screen that says
/// which. Pure (no WinForms) so the wording is unit-testable, mirroring <see cref="BreakdownSort"/>
/// and <see cref="UsageBreakdownLayout"/>.
/// </summary>
internal static class BreakdownDrillText
{
    /// <summary>
    /// How long a drilled-into name may be before it is shortened. The headings sit on one
    /// non-wrapping line next to the "Show all" button, and a project's display name is a full
    /// path ("C:\Users\…\src\some-very-long-project") that would otherwise run under it.
    /// </summary>
    internal const int MaxNameLength = 44;

    /// <summary>
    /// The heading above the model table: "By model" normally, or the models one project used
    /// when <paramref name="drilledProject"/> is the project selected in the project table.
    /// </summary>
    public static string ModelSection(string? drilledProject) =>
        drilledProject is null ? "By model" : $"Models used in {Shorten(drilledProject)}";

    /// <summary>
    /// The heading above the project table: "By project" normally, or the projects one model ran
    /// in when <paramref name="drilledModel"/> is the model selected in the model table.
    /// </summary>
    public static string ProjectSection(string? drilledModel) =>
        drilledModel is null ? "By project" : $"Projects using {Shorten(drilledModel)}";

    /// <summary>
    /// <paramref name="value"/> clipped to <see cref="MaxNameLength"/> characters with the middle
    /// elided. The middle rather than the tail because these are paths, and the leaf directory —
    /// the part that identifies the project — is at the end.
    /// </summary>
    internal static string Shorten(string value)
    {
        if (value.Length <= MaxNameLength)
            return value;

        // One character of the budget goes to the ellipsis; the odd one lands on the head, which
        // is where the drive/root lives.
        var keep = MaxNameLength - 1;
        var head = (keep + 1) / 2;
        return string.Concat(value.AsSpan(0, head), "…", value.AsSpan(value.Length - (keep - head)));
    }
}
