using System.Runtime.InteropServices;
using static TaskbarPlayer.Native;

namespace TaskbarPlayer;

/// <summary>
/// Crisp text on a transparent layered window.
/// GDI+ antialiasing ignores font hinting and looks thin and soft next to system text. So the text is drawn
/// with classic GDI (hinted, grayscale antialiasing, like the rest of the taskbar) white-on-black into an
/// offscreen mask, and the mask becomes the alpha channel of the text color in the final PARGB image.
/// </summary>
internal sealed class GdiTextLayer : IDisposable
{
    private readonly int _width, _height;
    private readonly IntPtr _dc, _dib, _oldBmp, _bits;

    public GdiTextLayer(int width, int height)
    {
        _width = width;
        _height = height;
        _dc = CreateCompatibleDC(IntPtr.Zero);
        var bi = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height, // top-down
            biPlanes = 1,
            biBitCount = 32,
        };
        // A fresh DIB section is zero-filled: black = no coverage
        _dib = CreateDIBSection(_dc, ref bi, 0, out _bits, IntPtr.Zero, 0);
        _oldBmp = SelectObject(_dc, _dib);
        SetBkMode(_dc, TRANSPARENT);
        SetTextColor(_dc, 0xFFFFFF);
    }

    public bool IsValid => _dib != IntPtr.Zero;

    /// <summary>Draws white text into the mask, clipped to <paramref name="clip"/>.</summary>
    public void DrawText(string text, IntPtr font, Rectangle layout, Rectangle clip, uint flags)
    {
        if (!IsValid || string.IsNullOrEmpty(text)) return;
        int saved = SaveDC(_dc);
        IntersectClipRect(_dc, clip.Left, clip.Top, clip.Right, clip.Bottom);
        SelectObject(_dc, font);
        var rc = new RECT { Left = layout.Left, Top = layout.Top, Right = layout.Right, Bottom = layout.Bottom };
        Native.DrawText(_dc, text, -1, ref rc, flags | DT_NOPREFIX);
        RestoreDC(_dc, saved);
    }

    /// <summary>
    /// Blends the mask over a PARGB image as solid <paramref name="color"/>.
    /// <paramref name="opacityAt"/> gives the opacity (0..255) for a pixel column (title vs. buttons).
    /// </summary>
    public void CompositeOnto(IntPtr target, Color color, byte[] gammaLut, Func<int, int> opacityAt)
    {
        if (!IsValid) return;
        int count = _width * _height * 4;
        var mask = new byte[count];
        var dst = new byte[count];
        Marshal.Copy(_bits, mask, 0, count);
        Marshal.Copy(target, dst, 0, count);

        var opacity = new int[_width];
        for (int x = 0; x < _width; x++) opacity[x] = opacityAt(x);

        for (int y = 0, i = 0; y < _height; y++)
        {
            for (int x = 0; x < _width; x++, i += 4)
            {
                int coverage = mask[i + 1]; // grayscale AA: R = G = B
                if (coverage == 0) continue;
                int a = gammaLut[coverage] * opacity[x] / 255;
                if (a == 0) continue;
                int inv = 255 - a;
                // Premultiplied "source over": out = src * a + dst * (1 - a)
                dst[i + 0] = (byte)((color.B * a + dst[i + 0] * inv) / 255);
                dst[i + 1] = (byte)((color.G * a + dst[i + 1] * inv) / 255);
                dst[i + 2] = (byte)((color.R * a + dst[i + 2] * inv) / 255);
                dst[i + 3] = (byte)(a + dst[i + 3] * inv / 255);
            }
        }
        Marshal.Copy(dst, 0, target, count);
    }

    /// <summary>
    /// Coverage-to-alpha curve. Linear alpha makes light-on-dark text look thinner than the system's own
    /// (which blends with gamma), so for the dark theme the coverage is boosted.
    /// </summary>
    public static byte[] BuildGammaLut(float gamma)
    {
        var lut = new byte[256];
        for (int i = 0; i < 256; i++)
            lut[i] = (byte)Math.Round(255 * Math.Pow(i / 255.0, 1.0 / gamma));
        return lut;
    }

    public void Dispose()
    {
        if (_oldBmp != IntPtr.Zero) SelectObject(_dc, _oldBmp);
        if (_dib != IntPtr.Zero) DeleteObject(_dib);
        DeleteDC(_dc);
    }
}
