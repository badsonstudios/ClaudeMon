namespace ClaudeMon.UI;

/// <summary>What the log should say about a health observation, if anything.</summary>
internal enum TaskbarHealLog
{
    /// <summary>Nothing new — same verdict as last time, or a repeat inside the WARN dampener.</summary>
    None,

    /// <summary>Back to normal after a fault.</summary>
    Recovered,

    /// <summary>Deliberately hidden under a fullscreen app (#123) — not a fault, but worth recording.</summary>
    Suppressed,

    /// <summary>No taskbar to sit on right now — waiting, not broken.</summary>
    Waiting,

    /// <summary>A fault, tolerated for now.</summary>
    Fault,

    /// <summary>A fault that has gone on long enough: rebuilding.</summary>
    Rebuilding,
}

/// <summary>
/// The outcome of one health observation: whether to rebuild the readout, what (if anything) to
/// log, and the numbers the log line wants.
/// </summary>
internal readonly record struct TaskbarHealVerdict(
    bool Rebuild,
    TaskbarHealLog Log,
    TaskbarOverlayStatus PreviousStatus,
    int ConsecutiveUnhealthy);

/// <summary>
/// Remembers how each readout has been behaving and turns a stream of health verdicts into
/// "keep it / rebuild it" plus a log line worth writing. Separated from
/// <see cref="TaskbarOverlayManager"/> — which needs a live desktop and so can never be tested —
/// because this state machine is where the interesting mistakes live: rebuilding on a single
/// transient bad check, rebuilding in a loop, or drowning the log in one line every two seconds.
/// Time is passed in rather than read, so the sequences can be tested without waiting for them.
/// </summary>
internal sealed class TaskbarHealTracker
{
    private sealed class DeviceState
    {
        public int ConsecutiveUnhealthy;
        public TaskbarOverlayStatus LastStatus = TaskbarOverlayStatus.Healthy;
        public long? LastRebuildTicks;
        public long? LastFaultLogTicks;
    }

    private readonly Dictionary<string, DeviceState> _states = new();

    /// <summary>
    /// Record one health verdict for one readout and decide what to do about it.
    /// <paramref name="nowTicks"/> is a monotonic millisecond count
    /// (<see cref="Environment.TickCount64"/> in production).
    /// </summary>
    internal TaskbarHealVerdict Observe(string device, TaskbarOverlayStatus status, long nowTicks)
    {
        if (!_states.TryGetValue(device, out var state))
            _states[device] = state = new DeviceState();

        var previous = state.LastStatus;
        var changed = status != previous;
        state.LastStatus = status;

        if (!TaskbarOverlayHealth.NeedsRebuild(status))
        {
            state.ConsecutiveUnhealthy = 0;
            // Re-arm the fault dampener, so the next episode is logged promptly rather than
            // being swallowed by the previous one's cooldown.
            state.LastFaultLogTicks = null;

            var log = !changed ? TaskbarHealLog.None : status switch
            {
                TaskbarOverlayStatus.SuppressedForFullscreen => TaskbarHealLog.Suppressed,
                TaskbarOverlayStatus.TaskbarMissing => TaskbarHealLog.Waiting,
                // Only worth an entry if it is recovering FROM something.
                _ => TaskbarOverlayHealth.NeedsRebuild(previous)
                    ? TaskbarHealLog.Recovered
                    : TaskbarHealLog.None,
            };

            return new TaskbarHealVerdict(Rebuild: false, log, previous, 0);
        }

        state.ConsecutiveUnhealthy++;

        var sinceRebuild = state.LastRebuildTicks is { } rebuilt ? nowTicks - rebuilt : (long?)null;
        if (TaskbarHealPolicy.ShouldRebuild(status, state.ConsecutiveUnhealthy, sinceRebuild))
        {
            var strikes = state.ConsecutiveUnhealthy;
            // A fresh window starts with a clean record; the cooldown is what stops the next
            // failure from turning into a rebuild loop.
            state.ConsecutiveUnhealthy = 0;
            state.LastStatus = TaskbarOverlayStatus.Healthy;
            state.LastRebuildTicks = nowTicks;
            state.LastFaultLogTicks = null;
            return new TaskbarHealVerdict(Rebuild: true, TaskbarHealLog.Rebuilding, previous, strikes);
        }

        // Dampened by time, not just by "the status changed": a readout flapping between two
        // faults would otherwise WARN on every tick of the rebuild cooldown.
        var sinceFaultLog = state.LastFaultLogTicks is { } logged ? nowTicks - logged : (long?)null;
        if (!TaskbarHealPolicy.ShouldLogFault(sinceFaultLog))
            return new TaskbarHealVerdict(false, TaskbarHealLog.None, previous, state.ConsecutiveUnhealthy);

        state.LastFaultLogTicks = nowTicks;
        return new TaskbarHealVerdict(false, TaskbarHealLog.Fault, previous, state.ConsecutiveUnhealthy);
    }

    /// <summary>
    /// Forget how many checks each readout has failed in a row, without forgetting when it was
    /// last rebuilt. Used after a resume or a detected process gap: whatever happened did not
    /// happen to the readouts, so an observation from before it must not combine with one after
    /// it into a rebuild nobody needed. The rebuild cooldown deliberately survives — a resume is
    /// not a licence to churn windows.
    /// </summary>
    internal void ClearStrikes()
    {
        foreach (var state in _states.Values)
            state.ConsecutiveUnhealthy = 0;
    }

    /// <summary>
    /// Drop the history of every device not in <paramref name="devices"/>, so a taskbar that
    /// goes away and comes back broken is diagnosed (and healed) as a fresh case.
    /// </summary>
    internal void RetainOnly(ICollection<string> devices)
    {
        foreach (var device in _states.Keys.Where(d => !devices.Contains(d)).ToList())
            _states.Remove(device);
    }

    internal void Clear() => _states.Clear();
}
