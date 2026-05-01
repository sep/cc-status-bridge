using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace ClaudeStatusBridge;

/// <summary>
/// Generates a tiny colored bitmap suitable for use as a tray icon.
/// We render a filled square at runtime rather than shipping ICO/PNG
/// assets per state — keeps the binary slim and lets us add states
/// without re-baking artwork.
/// </summary>
internal static class IconRenderer
{
    private const int Size = 32;

    public static readonly Color GrayDim   = Color.FromRgb(0x66, 0x6c, 0x70);
    public static readonly Color Green     = Color.FromRgb(0x4a, 0xa3, 0x4f);
    public static readonly Color Yellow    = Color.FromRgb(0xeb, 0xc4, 0x46);
    public static readonly Color Orange    = Color.FromRgb(0xe2, 0x8a, 0x2b);
    public static readonly Color Red       = Color.FromRgb(0xd9, 0x3a, 0x3a);
    public static readonly Color Blue      = Color.FromRgb(0x4a, 0x78, 0xc8);

    /// <summary>
    /// Map an aggregate-state string (TrayHost gets these from
    /// BridgeRunner) to the icon color to render. The hierarchy is:
    /// errors and blocks first (loudest), then active states, then
    /// idle, then nothing (no sessions / paused).
    /// </summary>
    public static Color ColorFor(string aggregate) => aggregate switch
    {
        "error"      => Red,
        "blocked"    => Orange,
        "compacting" => Blue,
        "working"    => Yellow,
        "thinking"   => Yellow,
        "idle"       => Green,
        _            => GrayDim,
    };

    public static WindowIcon RenderColored(Color color)
    {
        var bmp = new RenderTargetBitmap(new PixelSize(Size, Size), new Vector(96, 96));
        using (var ctx = bmp.CreateDrawingContext())
        {
            // Slight inset so the icon doesn't kiss the edges of the
            // tray cell on platforms that don't pad it.
            ctx.FillRectangle(new SolidColorBrush(color), new Rect(2, 2, Size - 4, Size - 4));
        }
        var ms = new MemoryStream();
        bmp.Save(ms);
        ms.Position = 0;
        return new WindowIcon(ms);
    }
}
