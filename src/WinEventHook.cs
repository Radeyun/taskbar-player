namespace TaskbarPlayer;

/// <summary>SetWinEventHook wrapper (out-of-context: the callback arrives through our thread's message queue).</summary>
internal sealed class WinEventHook : IDisposable
{
    private readonly Native.WinEventProc _proc; // keep the delegate alive so the GC does not collect it
    private IntPtr _hook;

    public WinEventHook(uint eventMin, uint eventMax, uint processId, Action<IntPtr, int> callback)
    {
        _proc = (_, _, hwnd, idObject, _, _, _) => callback(hwnd, idObject);
        _hook = Native.SetWinEventHook(eventMin, eventMax, IntPtr.Zero, _proc, processId, 0,
            Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);
        if (_hook == IntPtr.Zero) Log.Error($"SetWinEventHook(0x{eventMin:X}) failed");
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            Native.UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }
    }
}
