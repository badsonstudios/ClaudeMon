namespace ClaudeMon.Monitoring;

using ClaudeMon.Services;
using Microsoft.Win32;

/// <summary>
/// Tracks workstation lock state from session-switch events and raises
/// <see cref="Locked"/>/<see cref="Unlocked"/> exactly once per genuine
/// transition, so subscribers can pause/resume timers without worrying about
/// duplicate events double-starting them. Only lock/unlock is observed; other
/// session reasons (logon, remote connect, ...) are ignored by design
/// (issue #69 keeps idle detection out of scope). Owns and disposes the
/// <see cref="ISessionEvents"/> it watches.
///
/// The initial state is assumed unlocked: Windows offers no cheap "am I locked
/// right now?" query, so an app started into an already-locked session (e.g. a
/// delayed autostart racing a quick Win+L) polls until the next lock event.
/// </summary>
public sealed class SessionLockWatcher : IDisposable
{
    private readonly ISessionEvents _events;
    private readonly Logger? _logger;
    private bool _disposed;

    public bool IsLocked { get; private set; }

    public event EventHandler? Locked;
    public event EventHandler? Unlocked;

    public SessionLockWatcher(ISessionEvents events, Logger? logger = null)
    {
        _events = events;
        _logger = logger;
        _events.SessionSwitch += OnSessionSwitch;
    }

    private void OnSessionSwitch(object? sender, SessionSwitchReason reason)
    {
        switch (reason)
        {
            case SessionSwitchReason.SessionLock when !IsLocked:
                IsLocked = true;
                _logger?.Info("Session locked.");
                Locked?.Invoke(this, EventArgs.Empty);
                break;
            case SessionSwitchReason.SessionUnlock when IsLocked:
                IsLocked = false;
                _logger?.Info("Session unlocked.");
                Unlocked?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _events.SessionSwitch -= OnSessionSwitch;
        _events.Dispose();
    }
}