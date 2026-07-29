namespace ClaudeMon.Tests;

/// <summary>
/// Structural checks on the shipped application icon (#108). The .ico is a hand-written
/// multi-resolution container (<c>tools\icon\generate-claudemon-icon.ps1</c>) rather than
/// something a library produced, so the parts that are easy to get subtly wrong — entry count,
/// advertised sizes, payload bounds — are pinned here. Roslyn also parses the icon when it
/// embeds it via &lt;ApplicationIcon&gt;, so a malformed container fails the build too; these
/// tests localise the failure to the asset instead of a confusing compiler error.
/// </summary>
public class AppIconTests
{
    /// <summary>The five sizes the generator emits; 256 is advertised as 0 in the directory.</summary>
    private static readonly int[] ExpectedSizes = [16, 24, 32, 48, 256];

    private static string IconPath
    {
        get
        {
            var path = Path.Combine(AppContext.BaseDirectory, "ClaudeMon.ico");
            Assert.True(File.Exists(path), $"app icon missing from test output: {path}");
            return path;
        }
    }

    private static byte[] ReadIcon() => File.ReadAllBytes(IconPath);

    // Offsets/lengths are widened to long deliberately: uint arithmetic can wrap, which would
    // let the exact corrupt directory these bounds checks exist to catch slip through.
    private static (int Width, int Height, long Length, long Offset) Entry(byte[] b, int index)
    {
        var e = 6 + (16 * index);
        return (b[e], b[e + 1], BitConverter.ToUInt32(b, e + 8), BitConverter.ToUInt32(b, e + 12));
    }

    [Fact]
    public void Header_IsAnIconDirectory()
    {
        var b = ReadIcon();
        Assert.Equal(0, BitConverter.ToUInt16(b, 0));  // reserved
        Assert.Equal(1, BitConverter.ToUInt16(b, 2));  // type 1 = icon (2 would be a cursor)
    }

    [Fact]
    public void AdvertisesEveryExpectedSize()
    {
        var b = ReadIcon();
        Assert.Equal(ExpectedSizes.Length, BitConverter.ToUInt16(b, 4));

        for (var i = 0; i < ExpectedSizes.Length; i++)
        {
            var (w, h, _, _) = Entry(b, i);
            // 256 does not fit in the single width/height byte and is encoded as 0.
            var expected = ExpectedSizes[i] >= 256 ? 0 : ExpectedSizes[i];
            Assert.Equal(expected, w);
            Assert.Equal(expected, h);
        }
    }

    [Fact]
    public void EveryPayloadLiesWithinTheFile()
    {
        var b = ReadIcon();
        var count = BitConverter.ToUInt16(b, 4);
        var directoryEnd = 6 + (16 * count);

        for (var i = 0; i < count; i++)
        {
            var (_, _, len, off) = Entry(b, i);
            Assert.True(len > 0, $"entry {i} has an empty payload");
            Assert.True(off >= directoryEnd, $"entry {i} overlaps the directory");
            Assert.True(off + len <= b.Length, $"entry {i} runs past the end of the file");
        }
    }

    [Fact]
    public void PayloadsDoNotOverlap()
    {
        var b = ReadIcon();
        var count = BitConverter.ToUInt16(b, 4);
        var ranges = Enumerable.Range(0, count)
            .Select(i => Entry(b, i))
            .Select(e => (Start: e.Offset, End: e.Offset + e.Length))
            .OrderBy(r => r.Start)
            .ToList();

        for (var i = 1; i < ranges.Count; i++)
            Assert.True(ranges[i].Start >= ranges[i - 1].End, $"payload {i} overlaps its predecessor");
    }

    [Fact]
    public void LargestEntryIsAPng()
    {
        // The 256 entry is PNG-compressed; the smaller ones stay BMP for compatibility with
        // older icon consumers. (System.Drawing.Icon cannot select the PNG entry, which is why
        // this is checked by signature rather than by loading it.)
        var b = ReadIcon();
        var (_, _, len, off) = Entry(b, ExpectedSizes.Length - 1);
        Assert.True(len > 8);

        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        Assert.True(b.AsSpan((int)off, 8).SequenceEqual(signature), "256px entry is not a PNG");
    }

    [Fact]
    public void SmallEntriesAreLoadableBitmaps()
    {
        // Everything below 256 must be selectable through System.Drawing, which is the same
        // path older icon consumers take.
        foreach (var size in ExpectedSizes.Where(s => s < 256))
        {
            using var icon = new System.Drawing.Icon(IconPath, size, size);
            Assert.Equal(size, icon.Width);
            Assert.Equal(size, icon.Height);
        }
    }

    [Fact]
    public void ExecutableEmbedsTheIcon()
    {
        // Pins the <ApplicationIcon> wiring in ClaudeMon.csproj — one line, easy to lose in a
        // merge, and its loss is invisible until someone notices a generic taskbar button.
        // Windows synthesizes each window's taskbar and Alt-Tab icon from this resource
        // (WinForms sets no per-window icon on these dialogs), so it IS the delivery mechanism.
        var exe = Path.Combine(AppContext.BaseDirectory, "ClaudeMon.exe");
        Assert.True(File.Exists(exe), $"ClaudeMon.exe missing from test output: {exe}");

        using var embedded = System.Drawing.Icon.ExtractAssociatedIcon(exe);
        Assert.NotNull(embedded);

        using var bitmap = embedded.ToBitmap();
        // Sample inside the tile, clear of the glyph: the clay the generator paints (#D97757).
        var pixel = bitmap.GetPixel((int)(bitmap.Width * 0.15), (int)(bitmap.Height * 0.5));
        Assert.Equal((217, 119, 87), (pixel.R, pixel.G, pixel.B));
    }
}
