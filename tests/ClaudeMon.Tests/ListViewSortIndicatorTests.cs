namespace ClaudeMon.Tests;

using System.Windows.Forms;
using ClaudeMon.UI;

/// <summary>
/// Only the no-handle guard is exercised here. Everything past it is a conversation with a
/// live comctl32 header control (LVM_GETHEADER, then HDM_GET/SETITEMW against the HWND), which
/// needs a realized ListView window — i.e. a real desktop — so it is verified by using the
/// Usage &amp; costs window, not in the headless test host.
/// </summary>
public class ListViewSortIndicatorTests
{
    [Fact]
    public void Apply_HandleNotCreated_DoesNothingAndDoesNotForceTheHandle()
    {
        // The breakdown form calls this while building its columns. Reading list.Handle there
        // would realize the control mid-construction (and a later property change that forces a
        // handle recreation would silently drop the arrow anyway), so the guard has to come
        // first — and it must not itself be what creates the handle.
        using var list = new ListView();
        list.Columns.Add("Model");
        list.Columns.Add("Cost");

        ListViewSortIndicator.Apply(list, column: 1, ascending: false);

        Assert.False(list.IsHandleCreated);
    }

    [Fact]
    public void Apply_NoColumns_IsSafe()
    {
        using var list = new ListView();

        ListViewSortIndicator.Apply(list, column: 0, ascending: true);

        Assert.False(list.IsHandleCreated);
    }
}
