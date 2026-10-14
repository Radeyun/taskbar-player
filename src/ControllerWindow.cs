using System.Runtime.InteropServices;
using static TaskbarPlayer.Native;

namespace TaskbarPlayer;

/// <summary>
/// Hidden top-level window for broadcast messages.
/// "TaskbarCreated" and WM_SETTINGCHANGE are only sent to top-level windows,
/// so a message-only window (HWND_MESSAGE) would not get them.
/// </summary>
internal sealed class ControllerWindow : NativeWindow, IDisposable
{
    /// <summary>Window title other instances (installer/updater) use to find and close us.</summary>
    public const string Title = "TaskbarPlayer.Controller";

    private readonly int _taskbarCreatedMsg;

    public ControllerWindow()
    {
        _taskbarCreatedMsg = RegisterWindowMessage("TaskbarCreated");
        CreateHandle(new CreateParams
        {
            Caption = Title,
            Style = WS_POPUP, // no WS_VISIBLE: the window is never shown
            ExStyle = WS_EX_TOOLWINDOW,
        });
        // Even if we run elevated, explorer (normal integrity) can still reach us
        ChangeWindowMessageFilterEx(Handle, _taskbarCreatedMsg, MSGFLT_ALLOW, IntPtr.Zero);
    }

    public event Action? TaskbarCreated;
    public event Action<string?>? SettingChanged;
    public event Action? DisplayChanged;
    public event Action? ExitRequested;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == _taskbarCreatedMsg && _taskbarCreatedMsg != 0)
        {
            TaskbarCreated?.Invoke();
            return;
        }

        switch (m.Msg)
        {
            case WM_SETTINGCHANGE:
                // lParam names the changed area, e.g. "ImmersiveColorSet" (theme) or "TraySettings"
                SettingChanged?.Invoke(m.LParam != IntPtr.Zero ? Marshal.PtrToStringUni(m.LParam) : null);
                break;
            case WM_DISPLAYCHANGE:
            case WM_DPICHANGED:
                DisplayChanged?.Invoke();
                break;
            case WM_ENDSESSION:
                if (m.WParam != IntPtr.Zero) ExitRequested?.Invoke();
                break;
            case WM_CLOSE:
                // Sent by the installer/updater (or anyone) to ask for a clean exit that restores the taskbar
                ExitRequested?.Invoke();
                return;
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (Handle != IntPtr.Zero) DestroyHandle();
    }
}
