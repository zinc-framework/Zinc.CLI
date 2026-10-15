using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Zinc.Cli.Packaging;

/// <summary>
/// Makes the .exe's icon from the game's ZincAppIcon. Unlike the Mac's, which is a file in the
/// bundle, a Windows icon is a resource inside the executable, and the SDK only puts it there while
/// it builds the app host. So it's made before publishing and handed over as ApplicationIcon.
/// </summary>
static class WindowsIcon
{
    // What Windows picks from: small icons at 100-250% scale, up to Explorer's largest view.
    static readonly int[] Sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];

    /// <summary>The -p:ApplicationIcon argument for dotnet publish, or none if there's no icon to give it.</summary>
    public static async Task<string[]> PublishArgsAsync(GameProject project, string workDir, CancellationToken ct)
    {
        var props = await project.GetPropertiesAsync(["ZincAppIcon", "ApplicationIcon"], ct);
        // A game that sets its own .ico keeps it.
        if (props["ApplicationIcon"].Length > 0 || props["ZincAppIcon"].Length == 0) return [];
        if (!OperatingSystem.IsWindows()) return [];

        // ZincAppIcon is relative to the publish folder, which doesn't exist yet. Content keeps its
        // path from the project folder (res/icon.png publishes to res/icon.png), so look there.
        var icon = props["ZincAppIcon"];
        var source = Path.GetFullPath(icon, project.ProjectDirectory);
        if (!File.Exists(source))
        {
            Ui.Warn($"ZincAppIcon '{icon}' isn't at {Ui.Path(source)}, so the .exe gets the default icon.");
            return [];
        }

        var ico = Path.Combine(workDir, "app.ico");
        Ui.Step($"Making {Ui.Path(ico)} from {icon}");
        try
        {
            Write(source, ico);
        }
        catch (Exception e) when (e is ArgumentException or ExternalException)
        {
            // System.Drawing's way of saying it isn't an image it can read.
            throw new ZincException($"Couldn't read ZincAppIcon '{icon}' as an image: {e.Message}");
        }
        return [$"-p:ApplicationIcon={ico}"];
    }

    /// <summary>
    /// An .ico holding <paramref name="png"/> at each of <see cref="Sizes"/> up to its own size, each
    /// stored as a PNG (which Windows has read since Vista).
    /// </summary>
    [SupportedOSPlatform("windows")]
    static void Write(string png, string ico)
    {
        using var source = new Bitmap(png);
        var largest = Math.Max(source.Width, source.Height);
        int[] sizes = [.. Sizes.Where(s => s <= largest)];
        if (sizes.Length == 0) sizes = [largest];
        var images = sizes.Select(size => (size, data: Scale(source, size))).ToList();

        using var writer = new BinaryWriter(File.Create(ico));
        writer.Write((ushort)0); // reserved
        writer.Write((ushort)1); // an icon, not a cursor
        writer.Write((ushort)images.Count);
        var offset = 6 + 16 * images.Count;
        foreach (var (size, data) in images)
        {
            var side = (byte)(size >= 256 ? 0 : size); // 0 means 256
            writer.Write(side);
            writer.Write(side);
            writer.Write((byte)0);   // no palette
            writer.Write((byte)0);   // reserved
            writer.Write((ushort)1); // color planes
            writer.Write((ushort)32);
            writer.Write(data.Length);
            writer.Write(offset);
            offset += data.Length;
        }
        foreach (var (_, data) in images) writer.Write(data);
    }

    /// <summary><paramref name="source"/> fitted into a <paramref name="size"/> square, centered, as PNG bytes.</summary>
    [SupportedOSPlatform("windows")]
    static byte[] Scale(Bitmap source, int size)
    {
        var scale = (float)size / Math.Max(source.Width, source.Height);
        var (w, h) = (Math.Max(1, (int)MathF.Round(source.Width * scale)), Math.Max(1, (int)MathF.Round(source.Height * scale)));

        using var image = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(image))
        using (var edges = new ImageAttributes())
        {
            g.CompositingMode = CompositingMode.SourceCopy;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            // Without it, the filter samples past the edge and fades the outermost pixels.
            edges.SetWrapMode(WrapMode.TileFlipXY);
            g.DrawImage(source, new Rectangle((size - w) / 2, (size - h) / 2, w, h),
                0, 0, source.Width, source.Height, GraphicsUnit.Pixel, edges);
        }

        using var stream = new MemoryStream();
        image.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }
}
