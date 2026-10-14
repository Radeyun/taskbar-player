using System.Drawing.Drawing2D;
using System.Drawing.Text;

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
    private const string GlyphPrevious = "";
    private const string GlyphNext = "";
    private const string GlyphPlay = "";
    private const string GlyphPause = "";

    private static readonly bool HasGlyphFont =
        new InstalledFontCollection().Families.Any(f => f.Name == GlyphFont);

    private static float _fontScale;
    private static Font? _textFont, _glyphFont;

    public static void Draw(Graphics g, PlaqueLayout layout, float scale, PlaqueVisual v)
    {
        EnsureFonts(scale);

        // Almost transparent background (alpha = 1): invisible, but the window still gets the mouse —
        // fully transparent pixels of a layered window let clicks fall through.
        g.Clear(Color.FromArgb(1, 0, 0, 0));
        // ClearType is impossible on a transparent background, so use grayscale antialiasing
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        var fg = v.LightTheme ? Color.Black : Color.White;
        DrawTitle(g, layout, v, fg);
        DrawButtons(g, layout, v, fg);
    }

    private static void DrawTitle(Graphics g, PlaqueLayout layout, PlaqueVisual v, Color fg)
    {
        using var brush = new SolidBrush(Color.FromArgb(v.TextDimmed ? 130 : 255, fg));
        var rect = layout.TextRect;
        var oldClip = g.Clip;
        g.SetClip(rect);

        if (v.SlideFromText != null && v.SlideProgress < 1f)
        {
            // Ease-out cubic: fast start, soft stop
            float t = Compat.Clamp(v.SlideProgress, 0f, 1f);
            float e = 1f - (1f - t) * (1f - t) * (1f - t);
            float w = rect.Width;
            int dir = v.SlideDirection >= 0 ? 1 : -1;
            float oldDx = (float)Math.Round(-dir * e * w);       // old title slides out
            float newDx = (float)Math.Round(dir * (1f - e) * w); // new one slides in from the other side
            DrawText(g, v.SlideFromText, brush, Offset(rect, oldDx));
            DrawText(g, v.Text, brush, Offset(rect, newDx));
        }
        else
        {
            DrawText(g, v.Text, brush, rect);
        }

        g.Clip = oldClip;
        oldClip.Dispose();
    }

    private static void DrawText(Graphics g, string text, Brush brush, RectangleF rect)
    {
        using var format = new StringFormat(StringFormatFlags.NoWrap)
        {
            Alignment = StringAlignment.Far, // align the text against the buttons/tray
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
        };
        g.DrawString(text, _textFont!, brush, rect, format);
    }

    private static RectangleF Offset(RectangleF r, float dx) => new(r.X + dx, r.Y, r.Width, r.Height);

    private static void DrawButtons(Graphics g, PlaqueLayout layout, PlaqueVisual v, Color fg)
    {
        DrawButton(g, layout.Previous, PlaqueHit.Previous, GlyphPrevious, v, fg);
        DrawButton(g, layout.PlayPause, PlaqueHit.PlayPause, v.IsPlaying ? GlyphPause : GlyphPlay, v, fg);
        DrawButton(g, layout.Next, PlaqueHit.Next, GlyphNext, v, fg);
    }

    private static void DrawButton(Graphics g, Rectangle r, PlaqueHit id, string glyph, PlaqueVisual v, Color fg)
    {
        if (v.ButtonsEnabled && (v.Hover == id || v.Pressed == id))
        {
            // Highlight like taskbar buttons: translucent white (dark theme) / black (light theme)
            int alpha = v.Pressed == id ? 18 : 30;
            using var bg = new SolidBrush(Color.FromArgb(alpha, fg));
            g.FillRectangle(bg, r);
        }

        using var brush = new SolidBrush(Color.FromArgb(v.ButtonsEnabled ? 255 : 90, fg));
        if (HasGlyphFont)
        {
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            g.DrawString(glyph, _glyphFont!, brush, r, format);
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        }
        else
        {
            DrawShapeGlyph(g, r, id, v.IsPlaying, brush);
        }
    }

    /// <summary>Fallback without the MDL2 font: draw the icons with shapes.</summary>
    private static void DrawShapeGlyph(Graphics g, Rectangle r, PlaqueHit id, bool playing, Brush brush)
    {
        float s = r.Height * 0.25f; // icon size
        float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        switch (id)
        {
            case PlaqueHit.PlayPause when playing:
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
        g.SmoothingMode = SmoothingMode.None;
    }

    private static void EnsureFonts(float scale)
    {
        if (_textFont != null && Math.Abs(scale - _fontScale) < 0.001f) return;
        _textFont?.Dispose();
        _glyphFont?.Dispose();
        _textFont = new Font(AppConfig.FontFamily, AppConfig.FontSizePx * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        _glyphFont = HasGlyphFont
            ? new Font(GlyphFont, AppConfig.GlyphSizePx * scale, FontStyle.Regular, GraphicsUnit.Pixel)
            : null;
        _fontScale = scale;
    }
}
