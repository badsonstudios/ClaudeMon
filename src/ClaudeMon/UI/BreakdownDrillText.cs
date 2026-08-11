namespace ClaudeMon.UI;

/// <summary>
/// The section headings above the two "Usage &amp; costs" tables (#112). Each table is either
/// showing everything ("By model") or the counterpart of a row selected in the other one
/// ("Projects using claude-fable-5"), and the heading is the only thing on screen that says
/// which. Pure (no WinForms) so the wording is unit-testable, mirroring <c>BreakdownSort</c>
/// and <see cref="UsageBreakdownLayout"/>.
/// </summary>
internal static class BreakdownDrillText
{
    /// <summary>
    /// How long a drilled-into name may be before it is shortened. A project's display name is a
    /// full path ("C:\Users\…\src\some-very-long-project"), and the heading it goes in is one
    /// line. The label ellipsizes whatever still doesn't fit its measured width — this is the
    /// part that keeps the interesting <em>end</em> of a path, which a trailing "…" would eat.
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
    /// The heading above the cost-over-time chart (#113). The chart is deliberately whole-
    /// timeframe — per-project and per-model series are out of scope — so when a drill-down is
    /// running underneath, the heading says so rather than letting the chart be read as the
    /// selected row's spend. <paramref name="drilledName"/> is the drilled-into row's display
    /// name, or null when nothing is drilled into.
    /// </summary>
    public static string ChartSection(string? drilledName) =>
        drilledName is null
            ? "Cost per day"
            : $"Cost per day — everything, not just {Shorten(drilledName)}";

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
        // is where the drive/root lives. Both cuts step off a surrogate pair rather than through
        // it — half of an astral character renders as a replacement box.
        var keep = MaxNameLength - 1;
        var head = (keep + 1) / 2;
        if (head > 0 && char.IsHighSurrogate(value[head - 1]))
            head--;

        var tail = value.Length - (keep - head);
        if (tail < value.Length && char.IsLowSurrogate(value[tail]))
            tail++;

        return string.Concat(value.AsSpan(0, head), "…", value.AsSpan(tail));
    }
}
