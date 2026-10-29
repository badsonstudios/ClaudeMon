namespace ClaudeMon.Tests;

using System.Drawing;
using System.Drawing.Imaging;
using ClaudeMon.UI;

/// <summary>
/// The look is checked by eye on a real screen; these pin the lifecycle the frame timer relies
/// on — the window stops its timer the moment <see cref="TaskbarEffectScene.IsActive"/> is false.
/// </summary>
public class TaskbarEffectSceneTests
{
    private const float W = 60;
    private const float H = 40;

    private static void Run(TaskbarEffectScene scene, double seconds)
    {
        for (var t = 0.0; t < seconds; t += 1 / 30.0)
            scene.Step(1 / 30.0, W, H);
    }

    [Fact]
    public void AFreshSceneIsIdle()
    {
        using var scene = new TaskbarEffectScene(seed: 1);
        Assert.False(scene.IsActive);
    }

    [Theory]
    [InlineData(TaskbarLoopingEffect.LightSmoke)]
    [InlineData(TaskbarLoopingEffect.HeavySmoke)]
    [InlineData(TaskbarLoopingEffect.Fire)]
    public void ALoopStaysActiveThenBurnsOutAfterItStops(TaskbarLoopingEffect effect)
    {
        using var scene = new TaskbarEffectScene(seed: 1) { Looping = effect };
        Run(scene, 5);
        Assert.True(scene.IsActive);

        scene.Looping = TaskbarLoopingEffect.None;
        Run(scene, 5); // longer than any particle lives
        Assert.False(scene.IsActive);
    }

    [Fact]
    public void WithoutEmissionALoopBurnsOutButStaysActive()
    {
        // The hidden-readout path: nothing new spawns, but the loop is still wanted, so the
        // window keeps its (slow) poll running to bring it back when the readout reappears.
        using var scene = new TaskbarEffectScene(seed: 1) { Looping = TaskbarLoopingEffect.LightSmoke };
        Run(scene, 2);

        for (var i = 0; i < 10; i++)
            scene.Step(0.5, W, H, emit: false);

        Assert.True(scene.IsActive);
        scene.Looping = TaskbarLoopingEffect.None;
        Assert.False(scene.IsActive);
    }

    [Fact]
    public void TheNukeEndsOnItsOwn()
    {
        using var scene = new TaskbarEffectScene(seed: 1);
        scene.StartNuke();
        Assert.True(scene.NukeActive);

        Run(scene, TaskbarEffectScene.NukeDurationSeconds + 0.1);

        Assert.False(scene.NukeActive);
        Assert.False(scene.IsActive);
    }

    [Fact]
    public void ClearIsAHardStop()
    {
        using var scene = new TaskbarEffectScene(seed: 1) { Looping = TaskbarLoopingEffect.Fire };
        scene.StartNuke();
        Run(scene, 1);

        scene.Clear();

        Assert.False(scene.IsActive);
    }

    [Fact]
    public void TheCanvasFitsTheLoopAndTheWholeMushroomCloud()
    {
        using var scene = new TaskbarEffectScene(seed: 1);
        var loop = scene.Bounds(W, H);
        scene.StartNuke();
        var nuke = scene.Bounds(W, H);

        Assert.True(nuke.Contains(loop));
        Assert.True(nuke.Contains(RectangleF.FromLTRB(W / 2 - 3.3f * H, -4.9f * H, W / 2 + 3.3f * H, H)));
        // The canvas always contains the readout and never extends below the taskbar's bottom.
        Assert.True(loop.Contains(new RectangleF(0, 0, W, H)));
        Assert.Equal(H, nuke.Bottom);
    }

    [Fact]
    public void EveryEffectDrawsWithoutThrowing()
    {
        using var scene = new TaskbarEffectScene(seed: 1) { Looping = TaskbarLoopingEffect.Fire };
        scene.StartNuke();
        using var bitmap = new Bitmap(400, 400, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bitmap);
        var area = scene.Bounds(W, H);
        g.TranslateTransform(-area.Left, -area.Top);

        for (var i = 0; i < 100; i++)
        {
            scene.Step(1 / 30.0, W, H);
            g.Clear(Color.Transparent);
            scene.Draw(g, W, H);
        }

        scene.Looping = TaskbarLoopingEffect.LightSmoke;
        Run(scene, 1);
        scene.Draw(g, W, H);
    }
}
