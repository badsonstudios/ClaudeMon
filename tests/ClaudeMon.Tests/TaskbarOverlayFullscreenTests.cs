namespace ClaudeMon.Tests;

using System.Drawing;
using ClaudeMon.UI;

public class TaskbarOverlayFullscreenTests
{
    // A 1920x1080 monitor with a standard 48px bottom taskbar.
    private static readonly Rectangle Monitor = new(0, 0, 1920, 1080);
    private static readonly Rectangle Taskbar = new(0, 1032, 1920, 48);

    [Fact]
    public void Hides_WhenForegroundExactlyCoversTheMonitor()
    {
        Assert.True(TaskbarOverlayFullscreen.ShouldHide(
            Monitor, "UnityWndClass", ownProcess: false, Taskbar));
    }

    [Fact]
    public void Hides_WhenForegroundOverhangsTheMonitor()
    {
        // Borderless windows sometimes overscan by their invisible resize border.
        var overhang = new Rectangle(-8, -8, 1936, 1096);
        Assert.True(TaskbarOverlayFullscreen.ShouldHide(
            overhang, "TscShellContainerClass", ownProcess: false, Taskbar));
    }

    [Fact]
    public void ShowsForMaximizedWindow_WhichStopsAtTheWorkingArea()
    {
        // A maximized window's rect ends at the taskbar's top edge (plus ~8px of invisible
        // border), so it never covers the taskbar strip.
        var maximized = new Rectangle(-8, -8, 1936, 1048);
        Assert.False(TaskbarOverlayFullscreen.ShouldHide(
            maximized, "Chrome_WidgetWin_1", ownProcess: false, Taskbar));
    }

    [Fact]
    public void ShowsForMaximizedWindow_UnderTaskbarAutoHide()
    {
        // With taskbar auto-hide, the working area spans the full monitor, so a maximized
        // window is monitor-sized — but the hidden taskbar has slid mostly below the monitor
        // edge, so the window still doesn't cover the taskbar's rect. This is the case that
        // makes taskbar-containment the right test and monitor-containment the wrong one.
        var maximized = new Rectangle(-8, -8, 1936, 1096);
        var hiddenTaskbar = new Rectangle(0, 1078, 1920, 48);
        Assert.False(TaskbarOverlayFullscreen.ShouldHide(
            maximized, "Chrome_WidgetWin_1", ownProcess: false, hiddenTaskbar));
    }

    [Fact]
    public void Shows_WhenFullscreenIsOnAnotherMonitor()
    {
        var fullscreenOnSecond = new Rectangle(1920, 0, 2560, 1440);
        Assert.False(TaskbarOverlayFullscreen.ShouldHide(
            fullscreenOnSecond, "UnityWndClass", ownProcess: false, Taskbar));
    }

    [Fact]
    public void Hides_BothMonitors_WhenAWindowSpansThem()
    {
        var spanning = new Rectangle(0, 0, 4480, 1440);
        var secondTaskbar = new Rectangle(1920, 1392, 2560, 48);
        Assert.True(TaskbarOverlayFullscreen.ShouldHide(
            spanning, "UnityWndClass", ownProcess: false, Taskbar));
        Assert.True(TaskbarOverlayFullscreen.ShouldHide(
            spanning, "UnityWndClass", ownProcess: false, secondTaskbar));
    }

    [Theory]
    [InlineData("Progman")]
    [InlineData("WorkerW")]
    [InlineData("Shell_TrayWnd")]
    [InlineData("Shell_SecondaryTrayWnd")]
    [InlineData("XamlExplorerHostIslandWindow")]
    [InlineData("MultitaskingViewFrame")]
    [InlineData("Windows.UI.Core.CoreWindow")]
    public void Shows_ForShellSurfaces_EvenWhenMonitorSized(string shellClass)
    {
        Assert.False(TaskbarOverlayFullscreen.ShouldHide(
            Monitor, shellClass, ownProcess: false, Taskbar));
    }

    [Fact]
    public void ShellExclusion_IsCaseSensitive_SoALookalikeGameClassStillHides()
    {
        Assert.True(TaskbarOverlayFullscreen.ShouldHide(
            Monitor, "PROGMAN", ownProcess: false, Taskbar));
    }

    [Fact]
    public void EmptyClassName_FailsTowardHiding()
    {
        // The live gatherer bails out (stays visible) when GetClassName itself fails; but if
        // an anonymous monitor-covering window reaches the pure rule, it hides — an unknown
        // class is not shell furniture.
        Assert.True(TaskbarOverlayFullscreen.ShouldHide(
            Monitor, "", ownProcess: false, Taskbar));
    }

    [Fact]
    public void Shows_ForOurOwnWindows()
    {
        Assert.False(TaskbarOverlayFullscreen.ShouldHide(
            Monitor, "WindowsForms10.Window.8.app.0", ownProcess: true, Taskbar));
    }

    [Fact]
    public void Shows_WhenTaskbarBoundsAreEmpty()
    {
        Assert.False(TaskbarOverlayFullscreen.ShouldHide(
            Monitor, "UnityWndClass", ownProcess: false, Rectangle.Empty));
    }

    [Fact]
    public void Shows_WhenForegroundStopsOnePixelShortOfTheTaskbar()
    {
        var almost = new Rectangle(0, 0, 1920, 1079);
        Assert.False(TaskbarOverlayFullscreen.ShouldHide(
            almost, "UnityWndClass", ownProcess: false, Taskbar));
    }
}
