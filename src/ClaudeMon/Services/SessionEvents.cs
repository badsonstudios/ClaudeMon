namespace ClaudeMon.Services;

using Microsoft.Win32;

/// <summary>
/// Thin seam over the Windows session-switch notification so the lock/unlock
/// state logic (<see cref="Monitoring.SessionLockWatcher"/>) can be unit-tested
/// with a fake instead of the static <see cref="SystemEvents"/> API.
/// </summary>
public interface ISessionEvents : IDisposable
{
    event EventHandler<SessionSwitchReason>? SessionSwitch;
}

/// <summary>
/// Production implementation backed by <see cref="SystemEvents.SessionSwitch"/>.
/// SystemEvents are static, so the subscription pins this object (and anything
/// its handlers reach) for the process lifetime unless detached — Dispose must
/// be called on shutdown. Requires a message loop on the subscribing thread,
/// which the WinForms tray app provides.
/// </summary>
public sealed class SystemSessionEvents : ISessionEvents
{
    private bool _disposed;

    public event EventHandler<SessionSwitchReason>? SessionSwitch;

    public SystemSessionEvents()
    {
        SystemEvents.SessionSwitch += OnSessionSwitch;
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e) =>
        SessionSwitch?.Invoke(this, e.Reason);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
    }
}