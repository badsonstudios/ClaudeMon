namespace ClaudeMon.UI;

using ClaudeMon.Models;
using ClaudeMon.Monitoring;

/// <summary>
/// The looping escalation effect a readout shows, one per usage level above green and ordered
/// mildest first (the nuke is a one-shot, not a loop).
/// </summary>
public enum TaskbarLoopingEffect
{
    None,

    /// <summary>Yellow: a light grey smoke.</summary>
    LightSmoke,

    /// <summary>Orange: bigger, darker smoke.</summary>
    HeavySmoke,

    /// <summary>Red: fire.</summary>
    Fire,
}

/// <summary>
/// One limit as the escalation effects see it: its stable <paramref name="Key"/>, the colour
/// level the readout's own logic gives it, its utilisation, and the reset time that identifies
/// its current window (null when unknown).
/// </summary>
internal readonly record struct TaskbarEffectReading(
    string Key,
    UsageLevel Level,
    double Percent,
    DateTimeOffset? ResetsAt);

/// <summary>What the readouts should show after an observation: a looping effect, and whether to fire the nuke.</summary>
internal readonly record struct TaskbarEffectDecision(TaskbarLoopingEffect Looping, bool PlayNuke);

/// <summary>
/// The escalation-effect state machine (issue #208): light smoke while a limit is yellow, heavy
/// smoke while it's orange, fire while it's red, a one-shot nuke when it reaches 100%,
/// click-to-dismiss, and re-arming when the limit's window resets. The worst limit wins. Kept
/// free of WinForms — the windows that draw the effects need a live desktop and can't be
/// tested, so every rule lives here instead.
/// </summary>
/// <remarks>
/// Dismissal is per limit and silences the level showing and everything milder, never anything
/// worse: dismissing light smoke leaves heavy smoke and fire armed, dismissing fire silences
/// both smokes too. It lasts until that limit's <c>resets_at</c> moves to a new window. A click
/// dismisses every limit that is contributing an effect at that moment — not just the worst
/// one — so silencing one limit's smoke doesn't immediately reveal another's.
/// </remarks>
internal sealed class TaskbarEffectTracker
{
    internal const string SessionKey = "session";
    internal const string WeeklyKey = "weekly";

    private sealed class LimitState
    {
        public DateTimeOffset? ResetsAt;
        // The highest level dismissed this window; only a worse level shows again.
        public UsageLevel DismissedThrough = UsageLevel.Green;
        public bool NukePlayed;

        // Not persisted: the level is re-read from every poll, and a limit not seen yet (or
        // missing from the latest poll) contributes nothing.
        public UsageLevel Level = UsageLevel.Green;
    }

    private readonly Dictionary<string, LimitState> _limits = new();

    public TaskbarEffectTracker(TaskbarEffectState? initialState = null)
    {
        if (initialState?.Limits is null) return;

        foreach (var (key, saved) in initialState.Limits)
        {
            if (saved is null) continue;
            _limits[key] = new LimitState
            {
                ResetsAt = saved.ResetsAt,
                DismissedThrough = Enum.IsDefined(saved.DismissedThrough) ? saved.DismissedThrough : UsageLevel.Green,
                NukePlayed = saved.NukePlayed,
            };
        }
    }

    /// <summary>
    /// True when the persistable state has changed since <see cref="MarkSaved"/> — the caller's
    /// cue to write <see cref="GetState"/> to disk. Level changes alone don't count.
    /// </summary>
    public bool HasUnsavedChanges { get; private set; }

    /// <summary>The looping effect the current state calls for.</summary>
    public TaskbarLoopingEffect Current => _limits.Values.Select(EffectOf).DefaultIfEmpty().Max();

    /// <summary>
    /// Record a fresh poll. Limits absent from <paramref name="readings"/> stop contributing an
    /// effect but keep their window memory, so a limit that drops out of one response and comes
    /// back in the same window doesn't re-arm. <see cref="TaskbarEffectDecision.PlayNuke"/> is
    /// true at most once per limit per window, and is marked as played the moment it's returned.
    /// </summary>
    public TaskbarEffectDecision Observe(IEnumerable<TaskbarEffectReading> readings)
    {
        foreach (var state in _limits.Values)
            state.Level = UsageLevel.Green;

        var playNuke = false;
        foreach (var reading in readings)
        {
            if (!_limits.TryGetValue(reading.Key, out var state))
            {
                _limits[reading.Key] = state = new LimitState { ResetsAt = reading.ResetsAt };
                HasUnsavedChanges = true;
            }
            else if (IsNewWindow(state.ResetsAt, reading.ResetsAt))
            {
                // The window rolled over: everything dismissed or played in the old one re-arms.
                state.ResetsAt = reading.ResetsAt;
                state.DismissedThrough = UsageLevel.Green;
                state.NukePlayed = false;
                HasUnsavedChanges = true;
            }
            else if (state.ResetsAt is null && reading.ResetsAt is not null)
            {
                // First time the window is identifiable. There's no evidence it's a new one, so
                // the memory stands — it just gains the timestamp later resets are judged by.
                state.ResetsAt = reading.ResetsAt;
                HasUnsavedChanges = true;
            }

            state.Level = reading.Level;

            if (reading.Percent >= 100 && !state.NukePlayed)
            {
                state.NukePlayed = true;
                playNuke = true;
                HasUnsavedChanges = true;
            }
        }

        return new TaskbarEffectDecision(Current, playNuke);
    }

    /// <summary>
    /// The user clicked the readout: silence whatever each limit is showing, and everything
    /// milder. Returns false (and changes nothing) when no effect was showing, so a plain click
    /// to open the flyout stays just that.
    /// </summary>
    public bool Dismiss()
    {
        var dismissed = false;
        foreach (var state in _limits.Values)
        {
            if (EffectOf(state) == TaskbarLoopingEffect.None)
                continue;

            state.DismissedThrough = state.Level;
            dismissed = true;
        }

        if (dismissed)
            HasUnsavedChanges = true;
        return dismissed;
    }

    /// <summary>Snapshot of the persistable state (see <see cref="AppSettings.TaskbarEffectState"/>).</summary>
    public TaskbarEffectState GetState() => new(_limits.ToDictionary(
        kv => kv.Key,
        kv => new TaskbarEffectLimitState(
            kv.Value.ResetsAt, kv.Value.DismissedThrough, kv.Value.NukePlayed)));

    /// <summary>Acknowledge that <see cref="GetState"/> has been persisted.</summary>
    public void MarkSaved() => HasUnsavedChanges = false;

    private static TaskbarLoopingEffect EffectOf(LimitState state) =>
        state.Level > state.DismissedThrough ? EffectFor(state.Level) : TaskbarLoopingEffect.None;

    /// <summary>The effect each usage level calls for.</summary>
    internal static TaskbarLoopingEffect EffectFor(UsageLevel level) => level switch
    {
        UsageLevel.Yellow => TaskbarLoopingEffect.LightSmoke,
        UsageLevel.Orange => TaskbarLoopingEffect.HeavySmoke,
        UsageLevel.Red => TaskbarLoopingEffect.Fire,
        _ => TaskbarLoopingEffect.None,
    };

    // A reset time that moves by more than the API's jitter means a new window. An unknown time
    // on either side is no evidence of a reset — re-arming on it would replay the nuke whenever
    // one poll came back without a resets_at.
    private static bool IsNewWindow(DateTimeOffset? known, DateTimeOffset? observed) =>
        known is { } k && observed is { } o && (o - k).Duration() > LimitWindowTracker.ResetJitter;
}
