namespace ClaudeMon.Models;

/// <summary>
/// Which metrics the taskbar readout shows, as one value — the four Settings display toggles
/// bundled so they travel together instead of as a growing list of adjacent <c>bool</c>
/// parameters. More than one can be on at a time (a composition, dot-separated in the Numbers
/// style); the click-to-cycle gesture works in terms of single-metric selections (see
/// <c>UI.TaskbarMetricCycle</c>).
/// </summary>
public readonly record struct TaskbarMetricSelection(
    bool Session,
    bool Weekly,
    bool TimeToLimit,
    bool TimeToReset)
{
    /// <summary>The default readout: session usage only.</summary>
    public static TaskbarMetricSelection SessionOnly => For(TaskbarMetric.Session);

    /// <summary>The selection showing exactly <paramref name="metric"/> and nothing else.</summary>
    public static TaskbarMetricSelection For(TaskbarMetric metric) => new(
        Session: metric == TaskbarMetric.Session,
        Weekly: metric == TaskbarMetric.Weekly,
        TimeToLimit: metric == TaskbarMetric.TimeToLimit,
        TimeToReset: metric == TaskbarMetric.TimeToReset);

    /// <summary>Whether <paramref name="metric"/> is one of the metrics shown.</summary>
    public bool Shows(TaskbarMetric metric) => metric switch
    {
        TaskbarMetric.Session => Session,
        TaskbarMetric.Weekly => Weekly,
        TaskbarMetric.TimeToLimit => TimeToLimit,
        TaskbarMetric.TimeToReset => TimeToReset,
        _ => false,
    };

    /// <summary>How many metrics are shown (0 when the user turned everything off).</summary>
    public int Count =>
        (Session ? 1 : 0) + (Weekly ? 1 : 0) + (TimeToLimit ? 1 : 0) + (TimeToReset ? 1 : 0);

    /// <summary>Copy with <paramref name="metric"/> turned on or off; others untouched.</summary>
    public TaskbarMetricSelection With(TaskbarMetric metric, bool shown) => metric switch
    {
        TaskbarMetric.Session => this with { Session = shown },
        TaskbarMetric.Weekly => this with { Weekly = shown },
        TaskbarMetric.TimeToLimit => this with { TimeToLimit = shown },
        TaskbarMetric.TimeToReset => this with { TimeToReset = shown },
        _ => this,
    };
}
