namespace ClaudeMon.Tests;

using ClaudeMon.Monitoring;
using ClaudeMon.Services;
using Microsoft.Win32;

public class SessionLockWatcherTests
{
    // Fake session-event source: lets tests raise session-switch reasons directly
    // and observe whether the watcher is still subscribed / disposed us.
    private sealed class FakeSessionEvents : ISessionEvents
    {
        public event EventHandler<SessionSwitchReason>? SessionSwitch;

        public bool HasSubscribers => SessionSwitch is not null;
        public bool Disposed { get; private set; }

        public void Raise(SessionSwitchReason reason) =>
            SessionSwitch?.Invoke(this, reason);

        public void Dispose() => Disposed = true;
    }

    private static (SessionLockWatcher Watcher, FakeSessionEvents Events, Counts Counts) Create()
    {
        var events = new FakeSessionEvents();
        var watcher = new SessionLockWatcher(events);
        var counts = new Counts();
        watcher.Locked += (_, _) => counts.Locked++;
        watcher.Unlocked += (_, _) => counts.Unlocked++;
        return (watcher, events, counts);
    }

    private sealed class Counts
    {
        public int Locked;
        public int Unlocked;
    }

    [Fact]
    public void StartsUnlocked()
    {
        var (watcher, _, _) = Create();
        Assert.False(watcher.IsLocked);
    }

    [Fact]
    public void Lock_RaisesLockedOnce()
    {
        var (watcher, events, counts) = Create();

        events.Raise(SessionSwitchReason.SessionLock);

        Assert.True(watcher.IsLocked);
        Assert.Equal(1, counts.Locked);
        Assert.Equal(0, counts.Unlocked);
    }

    [Fact]
    public void DoubleLock_RaisesLockedOnlyOnce()
    {
        var (_, events, counts) = Create();

        events.Raise(SessionSwitchReason.SessionLock);
        events.Raise(SessionSwitchReason.SessionLock);

        Assert.Equal(1, counts.Locked);
    }

    [Fact]
    public void UnlockWithoutPriorLock_RaisesNothing()
    {
        var (watcher, events, counts) = Create();

        events.Raise(SessionSwitchReason.SessionUnlock);

        Assert.False(watcher.IsLocked);
        Assert.Equal(0, counts.Locked);
        Assert.Equal(0, counts.Unlocked);
    }

    [Fact]
    public void DoubleUnlock_RaisesUnlockedOnlyOnce()
    {
        var (watcher, events, counts) = Create();

        events.Raise(SessionSwitchReason.SessionLock);
        events.Raise(SessionSwitchReason.SessionUnlock);
        events.Raise(SessionSwitchReason.SessionUnlock);

        Assert.False(watcher.IsLocked);
        Assert.Equal(1, counts.Unlocked);
    }

    [Fact]
    public void RepeatedLockUnlockCycles_TrackEachTransition()
    {
        var (watcher, events, counts) = Create();

        events.Raise(SessionSwitchReason.SessionLock);
        events.Raise(SessionSwitchReason.SessionUnlock);
        events.Raise(SessionSwitchReason.SessionLock);
        events.Raise(SessionSwitchReason.SessionUnlock);

        Assert.False(watcher.IsLocked);
        Assert.Equal(2, counts.Locked);
        Assert.Equal(2, counts.Unlocked);
    }

    [Theory]
    [InlineData(SessionSwitchReason.SessionLogon)]
    [InlineData(SessionSwitchReason.SessionLogoff)]
    [InlineData(SessionSwitchReason.RemoteConnect)]
    [InlineData(SessionSwitchReason.RemoteDisconnect)]
    [InlineData(SessionSwitchReason.ConsoleConnect)]
    [InlineData(SessionSwitchReason.ConsoleDisconnect)]
    [InlineData(SessionSwitchReason.SessionRemoteControl)]
    public void OtherReasons_AreIgnored(SessionSwitchReason reason)
    {
        var (watcher, events, counts) = Create();

        events.Raise(reason);

        Assert.False(watcher.IsLocked);
        Assert.Equal(0, counts.Locked);
        Assert.Equal(0, counts.Unlocked);
    }

    [Fact]
    public void Dispose_DetachesHandlerAndDisposesEventSource()
    {
        var (watcher, events, counts) = Create();

        watcher.Dispose();

        Assert.False(events.HasSubscribers);
        Assert.True(events.Disposed);

        // A late event from an already-detached source must be inert.
        events.Raise(SessionSwitchReason.SessionLock);
        Assert.Equal(0, counts.Locked);
    }

    [Fact]
    public void Dispose_Twice_IsSafe()
    {
        var (watcher, events, _) = Create();

        watcher.Dispose();
        watcher.Dispose();

        Assert.True(events.Disposed);
    }
}
