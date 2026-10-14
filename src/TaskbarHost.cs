using static TaskbarPlayer.Native;

namespace TaskbarPlayer;

/// <summary>
/// Places the plaque on the taskbar.
/// Main mode (same trick as TrafficMonitor): Shell_TrayWnd → ReBarWindow32 → MSTaskSwWClass.
/// MSTaskSwWClass is narrowed by the plaque width and the plaque becomes a child of Shell_TrayWnd in the freed space.
/// Fallback mode: a topmost popup over the taskbar, left of TrayNotifyWnd.
/// </summary>
internal sealed class TaskbarHost : IDisposable
{
    public enum HostMode { None, Embedded, Fallback }

    private readonly PlaqueWindow _plaque;
    private readonly Func<bool> _render;
    private readonly SynchronizationContext _ui;

    private IntPtr _hTaskbar, _hRebar, _hTaskSw;
    private int _plaqueW;          // plaque width, physical px
    private int _shrunkWidth = -1; // width we gave MSTaskSwWClass
    private bool _embedFailed;     // embedding failed: use the fallback until explorer restarts
    private bool _inUpdate;
    private bool _updateQueued;
    private WinEventHook? _locationHook;
    private WinEventHook? _foregroundHook;

    public TaskbarHost(PlaqueWindow plaque, Func<bool> render, SynchronizationContext ui)
    {
        _plaque = plaque;
        _render = render;
        _ui = ui;
    }

    public HostMode Mode { get; private set; }
    public int Dpi { get; private set; } = 96;
    public float Scale => Dpi / 96f;

    /// <summary>The taskbar was recreated ("TaskbarCreated" message): embed again.</summary>
    public void OnTaskbarCreated()
    {
        Log.Info("TaskbarCreated");
        _embedFailed = false;
        Detach(restore: true);
        Update();
    }

    /// <summary>Deferred update (from hooks): coalesces bursts of events.</summary>
    public void QueueUpdate()
    {
        if (_updateQueued) return;
        _updateQueued = true;
        _ui.Post(_ =>
        {
            _updateQueued = false;
            Update();
        }, null);
    }

    /// <summary>Brings the plaque in line with the current taskbar state. Called from a timer and from events.</summary>
    public void Update()
    {
        if (_inUpdate) return;
        _inUpdate = true;
        try
        {
            UpdateCore();
        }
        catch (Exception ex)
        {
            Log.Error("TaskbarHost.Update", ex);
        }
        finally
        {
            _inUpdate = false;
        }
    }

    private void UpdateCore()
    {
        IntPtr hTaskbar = FindWindow("Shell_TrayWnd", null);
        if (hTaskbar == IntPtr.Zero)
        {
            // explorer is not running (or still starting): wait
            Detach(restore: false);
            return;
        }

        // A new taskbar instance, or our window died together with the old one
        if (Mode != HostMode.None && (hTaskbar != _hTaskbar || !_plaque.IsAlive))
            Detach(restore: hTaskbar == _hTaskbar);

        GetWindowRect(hTaskbar, out var tb);
        if (tb.Width < tb.Height)
        {
            // Vertical taskbar is not supported: just hide
            Detach(restore: true);
            return;
        }

        int dpi = (int)GetDpiForWindow(hTaskbar);
        if (dpi <= 0) dpi = 96;
        if (Mode != HostMode.None && dpi != Dpi)
            Detach(restore: true); // DPI changed: recompute the width and embed again

        switch (Mode)
        {
            case HostMode.None:
                Attach(hTaskbar, dpi);
                break;
            case HostMode.Embedded:
                if (!IsWindow(_hTaskSw) || !IsWindow(_hRebar)) Detach(restore: false);
                else LayoutEmbedded();
                break;
            case HostMode.Fallback:
                LayoutFallback();
                break;
        }
    }

    private void Attach(IntPtr hTaskbar, int dpi)
    {
        _hTaskbar = hTaskbar;
        Dpi = dpi;
        _plaqueW = (int)Math.Round(AppConfig.TotalWidthDip * Scale);
        _shrunkWidth = -1;

        if (!_embedFailed && !AppConfig.ForceFallback && TryEmbed())
        {
            Mode = HostMode.Embedded;
            GetWindowThreadProcessId(_hTaskbar, out uint pid);
            // React instantly to explorer window geometry changes (the timer is the safety net)
            _locationHook = new WinEventHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE, pid, OnLocationChange);
            Log.Info("Embedded into taskbar");
            return;
        }

        _embedFailed = true;
        _plaque.Create(); // a clean popup window
        Mode = HostMode.Fallback;
        // Clicking the taskbar raises it above us: restore topmost whenever the foreground window changes
        _foregroundHook = new WinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, 0, (_, _) => QueueUpdate());
        Log.Info("Using fallback topmost window");
        LayoutFallback();
    }

    private bool TryEmbed()
    {
        _hRebar = FindWindowEx(_hTaskbar, IntPtr.Zero, "ReBarWindow32", null);
        _hTaskSw = _hRebar != IntPtr.Zero ? FindWindowEx(_hRebar, IntPtr.Zero, "MSTaskSwWClass", null) : IntPtr.Zero;
        if (_hTaskSw == IntPtr.Zero)
        {
            Log.Error("ReBarWindow32/MSTaskSwWClass not found");
            return false;
        }

        _plaque.Create();
        if (!_plaque.EmbedInto(_hTaskbar))
            return false;

        Mode = HostMode.Embedded;
        if (LayoutEmbedded(forceRender: true))
            return true;

        // Did not work (e.g. layered child windows unsupported): roll back
        RestoreTaskList();
        _plaque.Destroy();
        Mode = HostMode.None;
        return false;
    }

    /// <returns>false if the plaque could not be drawn.</returns>
    private bool LayoutEmbedded(bool forceRender = false)
    {
        if (!GetWindowRect(_hTaskSw, out var sw)) return false;

        if (sw.Width != _shrunkWidth)
        {
            // The taskbar re-laid itself out (or this is the first pass): narrow MSTaskSwWClass again
            int newWidth = sw.Width - _plaqueW;
            if (newWidth < _plaqueW / 4)
            {
                _plaque.Hide(); // not enough room
                _shrunkWidth = -1;
                return true;
            }

            // MoveWindow takes coordinates in the parent's (ReBarWindow32) client area
            var rc = sw;
            MapWindowPoints(IntPtr.Zero, _hRebar, ref rc, 2);
            MoveWindow(_hTaskSw, rc.Left, rc.Top, newWidth, rc.Height, true);
            _shrunkWidth = newWidth;
            sw.Right = sw.Left + newWidth;
            forceRender = true;
        }

        // The plaque goes right of the narrowed task list, i.e. right next to the notification area.
        // Child window coordinates are Shell_TrayWnd client coordinates.
        var pr = new RECT { Left = sw.Right, Top = sw.Top, Right = sw.Right + _plaqueW, Bottom = sw.Bottom };
        MapWindowPoints(IntPtr.Zero, _hTaskbar, ref pr, 2);
        bool sizeChanged = _plaque.SetBounds(pr, HWND_TOP);

        return !(sizeChanged || forceRender) || _render();
    }

    private void LayoutFallback()
    {
        if (!_plaque.IsAlive) _plaque.Create();

        // Full-screen app: the taskbar is hidden, so hide too
        if (SHQueryUserNotificationState(out int state) == 0 &&
            state is QUNS_BUSY or QUNS_RUNNING_D3D_FULL_SCREEN or QUNS_PRESENTATION_MODE)
        {
            _plaque.Hide();
            return;
        }

        GetWindowRect(_hTaskbar, out var tb);
        int right = tb.Right;
        IntPtr hTray = FindWindowEx(_hTaskbar, IntPtr.Zero, "TrayNotifyWnd", null);
        if (hTray != IntPtr.Zero && GetWindowRect(hTray, out var tray)) right = tray.Left;

        var r = new RECT { Left = right - _plaqueW, Top = tb.Top, Right = right, Bottom = tb.Bottom };
        bool wasVisible = _plaque.IsVisible;
        // SetWindowPos(HWND_TOPMOST) raises the window above the taskbar again every time
        bool sizeChanged = _plaque.SetBounds(r, HWND_TOPMOST);
        if (sizeChanged || !wasVisible) _render();
    }

    private void OnLocationChange(IntPtr hwnd, int idObject)
    {
        if (idObject == OBJID_WINDOW && (hwnd == _hTaskSw || hwnd == _hRebar || hwnd == _hTaskbar))
            QueueUpdate();
    }

    /// <summary>Gives MSTaskSwWClass its original width back (unless the taskbar already changed it itself).</summary>
    private void RestoreTaskList()
    {
        if (_shrunkWidth < 0 || _hTaskSw == IntPtr.Zero || !IsWindow(_hTaskSw)) return;
        if (GetWindowRect(_hTaskSw, out var sw) && sw.Width == _shrunkWidth)
        {
            var rc = sw;
            MapWindowPoints(IntPtr.Zero, _hRebar, ref rc, 2);
            MoveWindow(_hTaskSw, rc.Left, rc.Top, sw.Width + _plaqueW, rc.Height, true);
            Log.Info("Task list width restored");
        }
        _shrunkWidth = -1;
    }

    private void Detach(bool restore)
    {
        _locationHook?.Dispose();
        _locationHook = null;
        _foregroundHook?.Dispose();
        _foregroundHook = null;

        if (Mode == HostMode.Embedded && restore) RestoreTaskList();
        _plaque.Destroy();

        Mode = HostMode.None;
        _hTaskbar = _hRebar = _hTaskSw = IntPtr.Zero;
        _shrunkWidth = -1;
    }

    /// <summary>
    /// Emergency restore of the task list width (from ProcessExit / an unhandled exception).
    /// Only Win32 calls that are safe from any thread.
    /// </summary>
    public void EmergencyRestore()
    {
        try
        {
            if (Mode == HostMode.Embedded) RestoreTaskList();
        }
        catch
        {
            // the process is going away anyway
        }
    }

    public void Dispose() => Detach(restore: true);
}
