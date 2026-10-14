using System.Drawing.Drawing2D;
using System.Drawing.Text;
using static TaskbarPlayer.Native;

namespace TaskbarPlayer;

/// <summary>Plaque area under the cursor.</summary>
internal enum PlaqueHit { None, Text, Previous, PlayPause, Next }

/// <summary>Plaque geometry: title area on the left, three buttons on the right (next to the tray).</summary>
internal sealed class PlaqueLayout
{
    public Size Size { get; private init; }
    /// <summary>Where the text is drawn (with padding).</summary>
    public RectangleF TextRect { get; private init; }
    /// <summary>The whole title area (for clicks and the tooltip).</summary>
    public Rectangle TextArea { get; private init; }
    public Rectangle Previous { get; private init; }
    public Rectangle PlayPause { get; private init; }
    public Rectangle Next { get; private init; }

    public static PlaqueLayout Compute(Size size, float scale)
    {
        int bw = (int)Math.Round(AppConfig.ButtonWidthDip * scale);
        int buttonsLeft = Math.Max(0, size.Width - bw * 3);
        float pad = AppConfig.PaddingDip * scale;
        return new PlaqueLayout
        {
            Size = size,
            TextArea = new Rectangle(0, 0, buttonsLeft, size.Height),
            TextRect = new RectangleF(pad, 0, Math.Max(1, buttonsLeft - pad - 2 * scale), size.Height),
            Previous = new Rectangle(buttonsLeft, 0, bw, size.Height),
            PlayPause = new Rectangle(buttonsLeft + bw, 0, bw, size.Height),
            Next = new Rectangle(buttonsLeft + bw * 2, 0, bw, size.Height),
        };
    }

    public PlaqueHit HitTest(Point p)
    {
        if (Previous.Contains(p)) return PlaqueHit.Previous;
        if (PlayPause.Contains(p)) return PlaqueHit.PlayPause;
        if (Next.Contains(p)) return PlaqueHit.Next;
        if (TextArea.Contains(p)) return PlaqueHit.Text;
        return PlaqueHit.None;
    }

    public static bool IsButton(PlaqueHit hit) => hit is PlaqueHit.Previous or PlaqueHit.PlayPause or PlaqueHit.Next;
}

/// <summary>Everything needed to draw one frame.</summary>
internal sealed record PlaqueVisual(
    string Text,
    bool TextDimmed,
    string? SlideFromText,  // old title that slides out (null = no animation)
    int SlideDirection,     // +1: next track (slides out to the left), -1: previous (slides out to the right)
    float SlideProgress,    // 0..1
    bool IsPlaying,
    bool ButtonsEnabled,
    PlaqueHit Hover,
    PlaqueHit Pressed,
    bool LightTheme);

/// <summary>Draws the plaque content.</summary>
internal static class PlaqueRenderer
{
    // Segoe MDL2 Assets glyphs (present on every Windows 10/11)
    private const string GlyphFont = "Segoe MDL2 Assets";
    private static readonly string GlyphPrevious = ((char)0xE892).ToString();
    private static readonly string GlyphNext = ((char)0xE893).ToString();
    private static readonly string GlyphPlay = ((char)0xE768).ToString();
    private static readonly string GlyphPause = ((char)0xE769).ToString();

    private static readonly bool HasGlyphFont =
        new InstalledFontCollection().Families.Any(f => f.Name == GlyphFont);

    private static readonly byte[] GammaDark = GdiTextLayer.BuildGammaLut(AppConfig.TextGammaDarkTheme);
    private static readonly byte[] GammaLight = GdiTextLayer.BuildGammaLut(AppConfig.TextGammaLightTheme);

    private static float _fontScale;
    private static IntPtr _textFont, _glyphFont; // GDI HFONTs, recreated when the DPI changes

    public static void Draw(Graphics g, IntPtr bits, PlaqueLayout layout, float scale, PlaqueVisual v)
    {
        EnsureFonts(scale);
        var fg = v.LightTheme ? Color.Black : Color.White;

        // 1. Background and hover highlights with GDI+.
        // Almost transparent background (alpha = 1): invisible, but the window still gets the mouse —
        // fully transparent pixels of a layered window let clicks fall through.
        g.Clear(Color.FromArgb(1, 0, 0, 0));
        DrawButtonBackground(g, layout.Previous, PlaqueHit.Previous, v, fg);
        DrawButtonBackground(g, layout.PlayPause, PlaqueHit.PlayPause, v, fg);
        DrawButtonBackground(g, layout.Next, PlaqueHit.Next, v, fg);
        if (!HasGlyphFont) DrawShapeGlyphs(g, layout, v, fg);
        g.Flush(System.Drawing.Drawing2D.FlushIntention.Sync); // pixels are touched directly below

        // 2. Title and button icons with hinted GDI text, blended in as an alpha mask
        using var text = new GdiTextLayer(layout.Size.Width, layout.Size.Height);
        DrawTitle(text, layout, v);
        if (HasGlyphFont)
        {
            const uint glyphFlags = DT_CENTER | DT_VCENTER | DT_SINGLELINE;
            text.DrawText(GlyphPrevious, _glyphFont, layout.Previous, layout.Previous, glyphFlags);
            text.DrawText(v.IsPlaying ? GlyphPause : GlyphPlay, _glyphFont, layout.PlayPause, layout.PlayPause, glyphFlags);
            text.DrawText(GlyphNext, _glyphFont, layout.Next, layout.Next, glyphFlags);
        }

        int titleOpacity = v.TextDimmed ? 130 : 255;
        int buttonOpacity = v.ButtonsEnabled ? 255 : 90;
        int buttonsLeft = layout.Previous.Left;
        text.CompositeOnto(bits, fg, v.LightTheme ? GammaLight : GammaDark,
            x => x < buttonsLeft ? titleOpacity : buttonOpacity);
    }

    private static void DrawTitle(GdiTextLayer text, PlaqueLayout layout, PlaqueVisual v)
    {
        // Align the title against the buttons/tray; "..." when it does not fit
        const uint flags = DT_RIGHT | DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS;
        var rect = Rectangle.Round(layout.TextRect);

        if (v.SlideFromText != null && v.SlideProgress < 1f)
        {
            // Ease-out cubic: fast start, soft stop
            float t = Compat.Clamp(v.SlideProgress, 0f, 1f);
            float e = 1f - (1f - t) * (1f - t) * (1f - t);
            int dir = v.SlideDirection >= 0 ? 1 : -1;
            int oldDx = (int)Math.Round(-dir * e * rect.Width);       // old title slides out
            int newDx = (int)Math.Round(dir * (1f - e) * rect.Width); // new one slides in from the other side
            text.DrawText(v.SlideFromText, _textFont, Offset(rect, oldDx), rect, flags);
            text.DrawText(v.Text, _textFont, Offset(rect, newDx), rect, flags);
        }
        else
        {
            text.DrawText(v.Text, _textFont, rect, rect, flags);
        }
    }

    private static Rectangle Offset(Rectangle r, int dx) => new(r.X + dx, r.Y, r.Width, r.Height);

    private static void DrawButtonBackground(Graphics g, Rectangle r, PlaqueHit id, PlaqueVisual v, Color fg)
    {
        if (!v.ButtonsEnabled || (v.Hover != id && v.Pressed != id)) return;
        // Highlight like taskbar buttons: translucent white (dark theme) / black (light theme)
        int alpha = v.Pressed == id ? 18 : 30;
        using var bg = new SolidBrush(Color.FromArgb(alpha, fg));
        g.FillRectangle(bg, r);
    }

    /// <summary>Fallback without the MDL2 font: draw the icons with shapes.</summary>
    private static void DrawShapeGlyphs(Graphics g, PlaqueLayout layout, PlaqueVisual v, Color fg)
    {
        using var brush = new SolidBrush(Color.FromArgb(v.ButtonsEnabled ? 255 : 90, fg));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        foreach (var (r, id) in new[] { (layout.Previous, PlaqueHit.Previous), (layout.PlayPause, PlaqueHit.PlayPause), (layout.Next, PlaqueHit.Next) })
        {
            float s = r.Height * 0.25f; // icon size
            float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
            switch (id)
            {
                case PlaqueHit.PlayPause when v.IsPlaying:
                    g.FillRectangle(brush, cx - s * 0.45f, cy - s / 2, s * 0.3f, s);
                    g.FillRectangle(brush, cx + s * 0.15f, cy - s / 2, s * 0.3f, s);
                    break;
                case PlaqueHit.PlayPause:
                    g.FillPolygon(brush, new[] { new PointF(cx - s * 0.4f, cy - s / 2), new PointF(cx + s * 0.5f, cy), new PointF(cx - s * 0.4f, cy + s / 2) });
                    break;
                case PlaqueHit.Next:
                    g.FillPolygon(brush, new[] { new PointF(cx - s / 2, cy - s / 2), new PointF(cx + s * 0.3f, cy), new PointF(cx - s / 2, cy + s / 2) });
                    g.FillRectangle(brush, cx + s * 0.3f, cy - s / 2, s * 0.2f, s);
                    break;
                case PlaqueHit.Previous:
                    g.FillPolygon(brush, new[] { new PointF(cx + s / 2, cy - s / 2), new PointF(cx - s * 0.3f, cy), new PointF(cx + s / 2, cy + s / 2) });
                    g.FillRectangle(brush, cx - s / 2, cy - s / 2, s * 0.2f, s);
                    break;
            }
        }
        g.SmoothingMode = SmoothingMode.None;
    }

    private static void EnsureFonts(float scale)
    {
        if (_textFont != IntPtr.Zero && Math.Abs(scale - _fontScale) < 0.001f) return;
        if (_textFont != IntPtr.Zero) DeleteObject(_textFont);
        if (_glyphFont != IntPtr.Zero) DeleteObject(_glyphFont);
        // Negative height = character height in pixels (12 px = 9 pt at 96 DPI, like the taskbar clock)
        _textFont = CreateGdiFont(AppConfig.FontFamily, AppConfig.FontSizePx * scale);
        _glyphFont = CreateGdiFont(GlyphFont, AppConfig.GlyphSizePx * scale);
        _fontScale = scale;
    }

    private static IntPtr CreateGdiFont(string face, float pixels) =>
        CreateFont(-(int)Math.Round(pixels), 0, 0, 0, FW_NORMAL, 0, 0, 0, DEFAULT_CHARSET, 0, 0,
            ANTIALIASED_QUALITY, 0, face);
}
