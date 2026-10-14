using Microsoft.Win32;
using static TaskbarPlayer.Native;
using Timer = System.Windows.Forms.Timer;

namespace TaskbarPlayer;

/// <summary>Ties together the media source (GSMTC), the plaque window, taskbar placement and mouse controls.</summary>
internal sealed class TrayApp : ApplicationContext
{
    private const string AppTitle = "Taskbar Player";
    private const string NothingPlayingText = "—";
    private const int AnimationFrameMs = 15;

    // Context menu command ids
    private const int CmdPlayPause = 1, CmdNext = 2, CmdPrevious = 3, CmdCopy = 4,
        CmdAutostart = 5, CmdUninstall = 6, CmdExit = 7;

    private readonly SynchronizationContext _ui;
    private readonly ControllerWindow _controller;
    private readonly PlaqueWindow _plaque;
    private readonly TaskbarHost _host;
    private readonly MediaService _media;
    private readonly Timer _layoutTimer, _pollTimer, _flashTimer, _slideTimer, _skipTimeoutTimer;

    private bool _lightTheme;
    private string? _flashText;
    private int _wheelAccum;
    private long _lastWheelSwitch;
    private bool _menuOpen;
    private bool _shutDown;

    /// <summary>What the plaque shows right now (may lag behind _media.Current while waiting for the next track).</summary>
    private MediaSnapshot _shown = MediaSnapshot.Empty;
    private PlaqueHit _hover, _pressed;

    // Waiting for the track to change after a next/previous command
    private int _pendingSkipDir;
    private string _pendingSkipKey = "";
    // Running slide animation
    private string? _slideFrom;
    private int _slideDir;
    private long _slideStart;

    public TrayApp()
    {
        _ui = SynchronizationContext.Current ?? throw new InvalidOperationException("No UI SynchronizationContext");
        _lightTheme = ReadLightTheme();

        _controller = new ControllerWindow();
        _controller.TaskbarCreated += () => _host!.OnTaskbarCreated();
        _controller.SettingChanged += OnSettingChanged;
        _controller.DisplayChanged += () => _host!.QueueUpdate();
        _controller.ExitRequested += Shutdown;

        _plaque = new PlaqueWindow(_controller.Handle);
        _plaque.MouseMoved += OnMouseMoved;
        _plaque.MouseLeft += OnMouseLeft;
        _plaque.LeftDown += OnLeftDown;
        _plaque.LeftUp += OnLeftUp;
        // Actions run outside the plaque's WndProc (its input queue is attached to explorer): via Post
        _plaque.MiddleClick += () => _ui.Post(_ => _media!.TogglePlayPause(), null);
        _plaque.RightClick += () => _ui.Post(_ => ShowMenu(), null);
        _plaque.Wheel += OnWheel;

        _host = new TaskbarHost(_plaque, Redraw, _ui);

        _media = new MediaService(_ui);
        _media.Changed += OnMediaChanged;

        _layoutTimer = new Timer { Interval = AppConfig.LayoutCheckIntervalMs };
        _layoutTimer.Tick += (_, _) => _host.Update();
        _pollTimer = new Timer { Interval = AppConfig.MediaPollIntervalMs };
        _pollTimer.Tick += (_, _) => _media.Poll();
        _flashTimer = new Timer { Interval = AppConfig.CopiedFeedbackMs };
        _flashTimer.Tick += (_, _) =>
        {
            _flashTimer.Stop();
            _flashText = null;
            Redraw();
        };
        _slideTimer = new Timer { Interval = AnimationFrameMs };
        _slideTimer.Tick += (_, _) => OnSlideFrame();
        _skipTimeoutTimer = new Timer { Interval = AppConfig.SkipPendingMs };
        _skipTimeoutTimer.Tick += (_, _) =>
        {
            // The track never changed (e.g. "previous" just rewound the current song): no animation
            ClearPendingSkip();
            OnMediaChanged();
        };

        _host.Update();
        _ = _media.StartAsync();
        _layoutTimer.Start();
        _pollTimer.Start();
    }

    // ---------- Track state and animation ----------

    private static string TrackKey(MediaSnapshot s) => s.HasMedia ? s.Artist + "\n" + s.Title : "";

    private static string TextOf(MediaSnapshot s) => s.HasMedia ? s.DisplayText : NothingPlayingText;

    /// <summary>The user asked for the next (+1) or previous (-1) track.</summary>
    private void Skip(int direction)
    {
        if (!_media.HasSession) return;

        // Remember which track was on screen: animate only if it really changes.
        // "Previous" in the middle of a song usually just rewinds it, so the title stays and there is no animation.
        _pendingSkipDir = direction;
        _pendingSkipKey = TrackKey(_shown);
        _skipTimeoutTimer.Stop();
        _skipTimeoutTimer.Start();

        if (direction > 0) _media.Next();
        else _media.Previous();
    }

    private void ClearPendingSkip()
    {
        _pendingSkipDir = 0;
        _skipTimeoutTimer.Stop();
    }

    private void OnMediaChanged()
    {
        var snap = _media.Current;

        if (_pendingSkipDir != 0)
        {
            if (!snap.HasMedia)
            {
                // While switching, the player may briefly report empty data:
                // keep the old title until the new one arrives (or the timeout fires)
                return;
            }
            if (TrackKey(snap) != _pendingSkipKey)
            {
                StartSlide(TextOf(_shown), _pendingSkipDir);
                ClearPendingSkip();
            }
            // Same track (rewound to start, status change): just update and keep waiting until the timeout
        }

        _shown = snap;
        Redraw();
    }

    private void StartSlide(string fromText, int direction)
    {
        // If a previous slide is still running, the new one starts from the current target title
        _slideFrom = fromText;
        _slideDir = direction;
        _slideStart = Compat.NowMs;
        _slideTimer.Start();
    }

    private float SlideProgress =>
        Compat.Clamp((Compat.NowMs - _slideStart) / (float)AppConfig.SlideDurationMs, 0f, 1f);

    private void OnSlideFrame()
    {
        if (_slideFrom == null || SlideProgress >= 1f)
        {
            _slideTimer.Stop();
            _slideFrom = null;
        }
        Redraw();
    }

    // ---------- Drawing ----------

    private PlaqueLayout CurrentLayout() => PlaqueLayout.Compute(_plaque.Size, _host.Scale);

    /// <summary>Redraws the plaque from the current state. Returns false if UpdateLayeredWindow failed.</summary>
    private bool Redraw()
    {
        if (!_plaque.IsAlive || _plaque.Size.IsEmpty) return true;

        var layout = CurrentLayout();
        var snap = _shown;
        bool flashing = _flashText != null;
        bool sliding = !flashing && _slideFrom != null && SlideProgress < 1f;
        bool playing = _media.Current.IsPlaying;
        bool enabled = _media.HasSession;

        var visual = new PlaqueVisual(
            Text: _flashText ?? TextOf(snap),
            TextDimmed: !flashing && (!snap.HasMedia || !snap.IsPlaying),
            SlideFromText: sliding ? _slideFrom : null,
            SlideDirection: _slideDir,
            SlideProgress: sliding ? SlideProgress : 1f,
            IsPlaying: playing,
            ButtonsEnabled: enabled,
            Hover: _hover,
            Pressed: _pressed,
            LightTheme: _lightTheme);

        _plaque.SetTool(1, layout.TextArea, snap.HasMedia ? snap.DisplayText : "");
        _plaque.SetTool(2, layout.Previous, enabled ? "Previous track" : "");
        _plaque.SetTool(3, layout.PlayPause, enabled ? (playing ? "Pause" : "Play") : "");
        _plaque.SetTool(4, layout.Next, enabled ? "Next track" : "");

        float scale = _host.Scale;
        return _plaque.Present((g, bits) => PlaqueRenderer.Draw(g, bits, layout, scale, visual));
    }

    // ---------- Mouse ----------

    private void OnMouseMoved(Point p)
    {
        var hit = CurrentLayout().HitTest(p);
        var hover = PlaqueLayout.IsButton(hit) ? hit : PlaqueHit.None;
        if (hover == _hover) return;
        _hover = hover;
        Redraw();
    }

    private void OnMouseLeft()
    {
        if (_hover == PlaqueHit.None && _pressed == PlaqueHit.None) return;
        _hover = PlaqueHit.None;
        _pressed = PlaqueHit.None;
        Redraw();
    }

    private void OnLeftDown(Point p)
    {
        var hit = CurrentLayout().HitTest(p);
        if (PlaqueLayout.IsButton(hit) && _media.HasSession)
        {
            _pressed = hit;
            Redraw();
        }
    }

    private void OnLeftUp(Point p)
    {
        var hit = CurrentLayout().HitTest(p);
        var pressed = _pressed;
        if (pressed != PlaqueHit.None)
        {
            _pressed = PlaqueHit.None;
            Redraw();
            // Fires only if released over the same button
            if (hit == pressed) _ui.Post(_ => OnButton(pressed), null);
            return;
        }
        if (!PlaqueLayout.IsButton(hit))
            _ui.Post(_ => CopyToClipboard(), null);
    }

    private void OnButton(PlaqueHit button)
    {
        switch (button)
        {
            case PlaqueHit.Previous: Skip(-1); break;
            case PlaqueHit.PlayPause: _media.TogglePlayPause(); break;
            case PlaqueHit.Next: Skip(+1); break;
        }
    }

    private void OnWheel(int delta)
    {
        // Accumulate the delta (touchpads send small values); one notch = 120
        _wheelAccum += delta;
        if (Math.Abs(_wheelAccum) < 120) return;
        int direction = _wheelAccum > 0 ? +1 : -1; // up = next, down = previous
        _wheelAccum = 0;

        long now = Compat.NowMs;
        if (now - _lastWheelSwitch < AppConfig.WheelThrottleMs) return;
        _lastWheelSwitch = now;

        _ui.Post(_ => Skip(direction), null);
    }

    private void CopyToClipboard()
    {
        string text = _media.Current.DisplayText;
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            // The clipboard may be held by another process: several attempts
            Clipboard.SetDataObject(text, true, 5, 100);
            _flashText = "Copied";
            _flashTimer.Stop();
            _flashTimer.Start();
            Redraw();
        }
        catch (Exception ex)
        {
            Log.Error("Clipboard", ex);
        }
    }

    // ---------- Context menu (native Win32 menu: DPI-correct and system-styled) ----------

    private void ShowMenu()
    {
        if (_menuOpen || !GetCursorPos(out var pt)) return;

        var snap = _media.Current;
        bool has = _media.HasSession;
        IntPtr menu = CreatePopupMenu();
        try
        {
            AddItem(menu, CmdPlayPause, snap.IsPlaying ? "Pause" : "Play", has);
            AddItem(menu, CmdNext, "Next track", has);
            AddItem(menu, CmdPrevious, "Previous track", has);
            AppendMenu(menu, MF_SEPARATOR, IntPtr.Zero, null);
            AddItem(menu, CmdCopy, "Copy", snap.HasMedia);
            AppendMenu(menu, MF_SEPARATOR, IntPtr.Zero, null);
            AddItem(menu, CmdAutostart, "Start with Windows", true, Installer.IsAutostartEnabled);
            if (Installer.IsInstalledCopy) AddItem(menu, CmdUninstall, "Uninstall", true);
            AddItem(menu, CmdExit, "Exit", true);

            // Like the tray icon pattern: our window must be foreground, or the menu won't close on an outside click
            SetForegroundWindow(_controller.Handle);
            var screen = Screen.FromPoint(new Point(pt.X, pt.Y)).Bounds;
            uint flags = TPM_RETURNCMD | TPM_NONOTIFY | TPM_RIGHTBUTTON | TPM_RIGHTALIGN;
            if (pt.Y > screen.Top + screen.Height / 2) flags |= TPM_BOTTOMALIGN;

            _menuOpen = true;
            int cmd = TrackPopupMenuEx(menu, flags, pt.X, pt.Y, _controller.Handle, IntPtr.Zero);
            _menuOpen = false;
            PostMessage(_controller.Handle, WM_NULL, IntPtr.Zero, IntPtr.Zero);
            OnMenuCommand(cmd);
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private static void AddItem(IntPtr menu, int id, string text, bool enabled, bool isChecked = false) =>
        AppendMenu(menu, MF_STRING | (enabled ? 0 : MF_GRAYED) | (isChecked ? MF_CHECKED : 0), new IntPtr(id), text);

    private void OnMenuCommand(int cmd)
    {
        switch (cmd)
        {
            case CmdPlayPause: _media.TogglePlayPause(); break;
            case CmdNext: Skip(+1); break;
            case CmdPrevious: Skip(-1); break;
            case CmdCopy: CopyToClipboard(); break;
            case CmdAutostart: ToggleAutostart(); break;
            case CmdUninstall: Uninstall(); break;
            case CmdExit: Shutdown(); break;
        }
    }

    private static void ToggleAutostart()
    {
        try
        {
            Installer.SetAutostart(!Installer.IsAutostartEnabled);
        }
        catch (Exception ex)
        {
            Log.Error("Autostart", ex);
            MessageBox.Show("Could not change the startup setting:\n\n" + ex.Message, AppTitle,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void Uninstall()
    {
        var answer = MessageBox.Show(
            "Uninstall Taskbar Player?\n\n" +
            $"It will be removed from startup and the folder\n{Installer.InstallDir}\nwill be deleted.",
            AppTitle, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes) return;

        try
        {
            Installer.ScheduleUninstall();
        }
        catch (Exception ex)
        {
            Log.Error("Uninstall", ex);
            MessageBox.Show("Uninstall failed:\n\n" + ex.Message, AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        Shutdown();
    }

    // ---------- Theme and shutdown ----------

    private void OnSettingChanged(string? area)
    {
        bool light = ReadLightTheme();
        if (light != _lightTheme)
        {
            _lightTheme = light;
            Redraw();
        }
        if (area == "TraySettings") _host.QueueUpdate();
    }

    /// <summary>Taskbar theme: SystemUsesLightTheme = 1 means light (dark by default).</summary>
    private static bool ReadLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int v && v == 1;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>For ProcessExit / crashes: put the taskbar back the way it was.</summary>
    public void EmergencyRestore()
    {
        if (!_shutDown) _host.EmergencyRestore();
    }

    private void Shutdown()
    {
        if (_shutDown) return;
        _shutDown = true;
        _layoutTimer.Stop();
        _pollTimer.Stop();
        _flashTimer.Stop();
        _slideTimer.Stop();
        _skipTimeoutTimer.Stop();
        _host.Dispose();   // restores the original MSTaskSwWClass width and destroys the plaque
        _media.Dispose();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Shutdown();
            _layoutTimer.Dispose();
            _pollTimer.Dispose();
            _flashTimer.Dispose();
            _slideTimer.Dispose();
            _skipTimeoutTimer.Dispose();
            _plaque.Dispose();
            _controller.Dispose();
        }
        base.Dispose(disposing);
    }
}
