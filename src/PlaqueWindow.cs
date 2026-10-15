using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using static TaskbarPlayer.Native;

namespace TaskbarPlayer;

/// <summary>
/// The plaque window: a layered window with per-pixel alpha (UpdateLayeredWindow) so the taskbar shows through.
/// Created as a popup; in embedded mode it becomes a child window of Shell_TrayWnd.
/// </summary>
internal sealed class PlaqueWindow : NativeWindow, IDisposable
{
    private readonly IntPtr _tooltipOwner;
    private IntPtr _tooltip;
    private readonly Dictionary<int, (Rectangle Rect, string Text)> _tools = new();
    private bool _trackingLeave;
    private bool _tooltipSuppressed;

    public PlaqueWindow(IntPtr tooltipOwner) => _tooltipOwner = tooltipOwner;

    /// <summary>Coordinates are plaque client coordinates in physical pixels.</summary>
    public event Action<Point>? MouseMoved;
    public event Action? MouseLeft;
    public event Action<Point>? LeftDown;
    public event Action<Point>? LeftUp;
    public event Action? MiddleClick;
    public event Action? RightClick;
    public event Action<int>? Wheel;

    /// <summary>Current window size in physical pixels.</summary>
    public Size Size { get; private set; }

    public bool IsAlive => Handle != IntPtr.Zero && IsWindow(Handle);

    public bool IsVisible => IsAlive && IsWindowVisible(Handle);

    /// <summary>Creates a new hidden popup window (destroying the old one, if any).</summary>
    public void Create()
    {
        Destroy();
        var cp = new CreateParams
        {
            Caption = "TaskbarPlayer",
            Style = WS_POPUP,
            // TOOLWINDOW: no taskbar button and not in Alt+Tab; NOACTIVATE: clicks do not steal focus
            ExStyle = WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            X = 0, Y = 0, Width = 1, Height = 1,
        };
        CreateHandle(cp);
        Size = Size.Empty;
        CreateTooltip();
    }

    /// <summary>Makes the window a child of <paramref name="parent"/> (a window of another process, explorer.exe).</summary>
    public bool EmbedInto(IntPtr parent)
    {
        if (!IsAlive) return false;
        // Switch popup -> child first, otherwise SetParent leaves it a top-level window with an owner
        SetWindowLong(Handle, GWL_STYLE, WS_CHILD | WS_CLIPSIBLINGS);
        SetParent(Handle, parent);
        if (GetParent(Handle) == parent) return true;

        Log.Error($"SetParent failed, win32 error {Marshal.GetLastWin32Error()}");
        return false;
    }

    /// <summary>
    /// Moves the window. Coordinates are parent client coordinates (child window) or screen coordinates (popup).
    /// Returns true when the size changed (a redraw is needed).
    /// </summary>
    public bool SetBounds(Native.RECT r, IntPtr insertAfter)
    {
        if (!IsAlive) return false;
        SetWindowPos(Handle, insertAfter, r.Left, r.Top, r.Width, r.Height,
            SWP_NOACTIVATE | SWP_SHOWWINDOW | SWP_NOOWNERZORDER);
        var newSize = new Size(r.Width, r.Height);
        bool changed = newSize != Size;
        Size = newSize;
        return changed;
    }

    public void Hide()
    {
        if (IsAlive) ShowWindow(Handle, SW_HIDE);
    }

    /// <summary>
    /// Draws the content: GDI+ paints straight into a 32-bit DIB (premultiplied ARGB),
    /// which is then handed to UpdateLayeredWindow.
    /// </summary>
    public bool Present(Action<Graphics, IntPtr> draw)
    {
        if (!IsAlive || Size.Width <= 0 || Size.Height <= 0) return false;

        int w = Size.Width, h = Size.Height;
        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        var bi = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = w,
            biHeight = -h, // negative height = top-down rows, same as GDI+
            biPlanes = 1,
            biBitCount = 32,
        };
        IntPtr dib = CreateDIBSection(screenDc, ref bi, 0, out IntPtr bits, IntPtr.Zero, 0);
        IntPtr oldBmp = IntPtr.Zero;
        try
        {
            if (dib == IntPtr.Zero) return false;
            oldBmp = SelectObject(memDc, dib);

            using (var bmp = new Bitmap(w, h, w * 4, PixelFormat.Format32bppPArgb, bits))
            using (var g = Graphics.FromImage(bmp))
            {
                draw(g, bits); // the renderer also writes pixels directly into the DIB
                g.Flush(System.Drawing.Drawing2D.FlushIntention.Sync);
            }

            var size = new SIZE { cx = w, cy = h };
            var src = new POINT();
            var blend = new BLENDFUNCTION
            {
                BlendOp = AC_SRC_OVER,
                SourceConstantAlpha = 255,
                AlphaFormat = AC_SRC_ALPHA, // per-pixel alpha
            };
            // pptDst = NULL: keep the position, it is set with SetWindowPos
            bool ok = UpdateLayeredWindow(Handle, screenDc, IntPtr.Zero, ref size, memDc, ref src, 0, ref blend, ULW_ALPHA);
            if (!ok) Log.Error($"UpdateLayeredWindow failed, win32 error {Marshal.GetLastWin32Error()}");
            return ok;
        }
        finally
        {
            if (oldBmp != IntPtr.Zero) SelectObject(memDc, oldBmp);
            if (dib != IntPtr.Zero) DeleteObject(dib);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>Adds or updates the tooltip for a rectangular area of the plaque (empty text = no tooltip).</summary>
    public void SetTool(int id, Rectangle rect, string text)
    {
        if (_tooltip == IntPtr.Zero) return;
        var ti = MakeToolInfo(id, rect, text);
        if (!_tools.TryGetValue(id, out var cur))
        {
            SendMessage(_tooltip, TTM_ADDTOOLW, IntPtr.Zero, ref ti);
        }
        else
        {
            if (cur.Rect != rect) SendMessage(_tooltip, TTM_NEWTOOLRECTW, IntPtr.Zero, ref ti);
            if (cur.Text != text) SendMessage(_tooltip, TTM_UPDATETIPTEXTW, IntPtr.Zero, ref ti);
        }
        _tools[id] = (rect, text);
    }

    private void CreateTooltip()
    {
        var icc = new INITCOMMONCONTROLSEX { dwSize = Marshal.SizeOf<INITCOMMONCONTROLSEX>(), dwICC = ICC_BAR_CLASSES };
        InitCommonControlsEx(ref icc);

        // The tooltip is owned by our hidden controller window, not by explorer's window
        _tooltip = CreateWindowEx(WS_EX_TOPMOST, TOOLTIPS_CLASS, null, WS_POPUP | TTS_NOPREFIX | TTS_ALWAYSTIP,
            CW_USEDEFAULT, CW_USEDEFAULT, CW_USEDEFAULT, CW_USEDEFAULT, _tooltipOwner, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (_tooltip == IntPtr.Zero) return;

        _tools.Clear();
        SendMessage(_tooltip, TTM_SETMAXTIPWIDTH, IntPtr.Zero, new IntPtr(800));
        // Longer hover delay than the default ~0.5 s, so the tooltip doesn't pop up over the title on a quick pass
        SendMessage(_tooltip, TTM_SETDELAYTIME, new IntPtr(TTDT_INITIAL), new IntPtr(AppConfig.TooltipDelayMs));
        SendMessage(_tooltip, TTM_SETDELAYTIME, new IntPtr(TTDT_RESHOW), new IntPtr(AppConfig.TooltipDelayMs / 2));
        _tooltipSuppressed = false;
    }

    /// <summary>
    /// Hides the tooltip and keeps it hidden until the cursor leaves the plaque:
    /// while the user scrolls or clicks, it would only cover the title they are looking at.
    /// </summary>
    private void SuppressTooltip()
    {
        if (_tooltip == IntPtr.Zero || _tooltipSuppressed) return;
        SendMessage(_tooltip, TTM_POP, IntPtr.Zero, IntPtr.Zero);
        SendMessage(_tooltip, TTM_ACTIVATE, IntPtr.Zero, IntPtr.Zero);
        _tooltipSuppressed = true;
    }

    private void RestoreTooltip()
    {
        if (_tooltip == IntPtr.Zero || !_tooltipSuppressed) return;
        SendMessage(_tooltip, TTM_ACTIVATE, new IntPtr(1), IntPtr.Zero);
        _tooltipSuppressed = false;
    }

    // TTF_SUBCLASS: the tooltip intercepts the plaque's mouse messages itself; each tool is a rectangle
    private TOOLINFO MakeToolInfo(int id, Rectangle r, string text) => new()
    {
        cbSize = Marshal.SizeOf<TOOLINFO>(),
        uFlags = TTF_SUBCLASS,
        hwnd = Handle,
        uId = new IntPtr(id),
        rect = new Native.RECT { Left = r.Left, Top = r.Top, Right = r.Right, Bottom = r.Bottom },
        lpszText = text,
    };

    private void DestroyTooltip()
    {
        if (_tooltip != IntPtr.Zero)
        {
            DestroyWindow(_tooltip);
            _tooltip = IntPtr.Zero;
        }
        _tools.Clear();
    }

    public void Destroy()
    {
        DestroyTooltip();
        if (Handle != IntPtr.Zero)
        {
            if (IsWindow(Handle)) DestroyHandle();
            else ReleaseHandle(); // already destroyed together with the taskbar (explorer restart)
        }
        Size = Size.Empty;
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_MOUSEACTIVATE:
                m.Result = new IntPtr(MA_NOACTIVATE);
                return;
            case WM_SETCURSOR:
                SetCursor(LoadCursor(IntPtr.Zero, new IntPtr(IDC_ARROW)));
                m.Result = new IntPtr(1);
                return;
            case WM_MOUSEMOVE:
                if (!_trackingLeave)
                {
                    // Ask for WM_MOUSELEAVE when the cursor leaves the window (to clear the hover highlight)
                    var tme = new TRACKMOUSEEVENT { cbSize = Marshal.SizeOf<TRACKMOUSEEVENT>(), dwFlags = TME_LEAVE, hwndTrack = Handle };
                    _trackingLeave = TrackMouseEvent(ref tme);
                }
                MouseMoved?.Invoke(PointFromLParam(m.LParam));
                return;
            case WM_MOUSELEAVE:
                _trackingLeave = false;
                RestoreTooltip();
                MouseLeft?.Invoke();
                return;
            case WM_LBUTTONDOWN:
            case WM_LBUTTONDBLCLK: // fast repeated clicks on a button must not get lost
                SuppressTooltip();
                SetCapture(Handle); // so we get WM_LBUTTONUP even if the button is released outside
                LeftDown?.Invoke(PointFromLParam(m.LParam));
                return;
            case WM_LBUTTONUP:
                ReleaseCapture();
                LeftUp?.Invoke(PointFromLParam(m.LParam));
                return;
            case WM_MBUTTONUP:
                SuppressTooltip();
                MiddleClick?.Invoke();
                return;
            case WM_RBUTTONUP:
                SuppressTooltip();
                RightClick?.Invoke();
                return;
            case WM_MOUSEWHEEL:
                SuppressTooltip();
                // High word of wParam is the wheel delta (a multiple of 120 per notch)
                Wheel?.Invoke((short)((m.WParam.ToInt64() >> 16) & 0xFFFF));
                return;
        }
        base.WndProc(ref m);
    }

    // Low/high words of lParam are signed X/Y (can be negative while the mouse is captured)
    private static Point PointFromLParam(IntPtr lParam)
    {
        long v = lParam.ToInt64();
        return new Point((short)(v & 0xFFFF), (short)((v >> 16) & 0xFFFF));
    }

    public void Dispose() => Destroy();
}
