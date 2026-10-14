using Windows.Foundation;
using Windows.Media.Control;
using Session = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;
using SessionManager = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager;

namespace TaskbarPlayer;

/// <summary>Snapshot of the current track.</summary>
internal sealed record MediaSnapshot(bool HasMedia, string Artist, string Title, bool IsPlaying)
{
    public static readonly MediaSnapshot Empty = new(false, "", "", false);

    /// <summary>"Artist — Title" (or just the title when the artist is unknown).</summary>
    public string DisplayText =>
        !HasMedia ? "" :
        string.IsNullOrWhiteSpace(Artist) ? Title :
        string.IsNullOrWhiteSpace(Title) ? Artist :
        $"{Artist} — {Title}";
}

/// <summary>
/// Current media session via Global System Media Transport Controls (no Spotify Web API, no login).
/// Spotify wins when it has a session, otherwise GetCurrentSession(). Updates are event-driven with a rare safety poll.
/// All public members are called, and events raised, on the UI thread.
/// </summary>
internal sealed class MediaService : IDisposable
{
    private readonly SynchronizationContext _ui;
    private SessionManager? _manager;
    private Session? _session;
    private string? _sessionId;
    private bool _starting;
    private bool _refreshQueued;
    private bool _rebindQueued;
    private int _version;
    private bool _disposed;

    public MediaService(SynchronizationContext ui) => _ui = ui;

    public MediaSnapshot Current { get; private set; } = MediaSnapshot.Empty;

    public bool HasSession => _session != null;

    public event Action? Changed;

    public async Task StartAsync()
    {
        if (_manager != null || _starting || _disposed) return;
        _starting = true;
        try
        {
            var manager = await SessionManager.RequestAsync();
            if (_disposed) return;
            _manager = manager;
            _manager.SessionsChanged += OnManagerEvent;
            _manager.CurrentSessionChanged += OnManagerEvent;
        }
        catch (Exception ex)
        {
            Log.Error("GSMTC RequestAsync", ex);
        }
        finally
        {
            _starting = false;
        }
        QueueRefresh(rebind: true);
    }

    /// <summary>Safety-net poll from a timer.</summary>
    public void Poll()
    {
        if (_manager == null) _ = StartAsync();
        else QueueRefresh(rebind: false);
    }

    public void TogglePlayPause() => RunCommand(s => s.TryTogglePlayPauseAsync());
    public void Next() => RunCommand(s => s.TrySkipNextAsync());
    public void Previous() => RunCommand(s => s.TrySkipPreviousAsync());

    private async void RunCommand(Func<Session, IAsyncOperation<bool>> command)
    {
        var session = _session;
        if (session == null) return;
        try
        {
            await command(session);
        }
        catch (Exception ex)
        {
            // The player may have closed between picking the session and the command
            Log.Error("Media command", ex);
            QueueRefresh(rebind: true);
        }
    }

    // WinRT events arrive on a background thread: hop to the UI thread.
    private void OnManagerEvent(SessionManager sender, object args) =>
        _ui.Post(_ => QueueRefresh(rebind: true), null);

    private void OnSessionEvent(Session sender, object args) =>
        _ui.Post(_ => QueueRefresh(rebind: false), null);

    /// <summary>Coalesces a burst of events (a track change fires several) into a single refresh.</summary>
    private void QueueRefresh(bool rebind)
    {
        if (_disposed) return;
        _rebindQueued |= rebind;
        if (_refreshQueued) return;
        _refreshQueued = true;
        _ui.Post(_ =>
        {
            _refreshQueued = false;
            bool rb = _rebindQueued;
            _rebindQueued = false;
            _ = RefreshAsync(rb);
        }, null);
    }

    private async Task RefreshAsync(bool rebind)
    {
        int version = ++_version;
        var manager = _manager;
        if (manager == null || _disposed) return;

        Session? session = null;
        try
        {
            session = PickSession(manager);
        }
        catch (Exception ex)
        {
            Log.Error("GetSessions", ex);
        }

        string? id = SafeId(session);
        if (rebind || session == null || id != _sessionId)
            Bind(session, id);

        if (session == null)
        {
            SetCurrent(MediaSnapshot.Empty);
            return;
        }

        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
            var info = session.GetPlaybackInfo();
            if (version != _version || _disposed) return; // a newer refresh started meanwhile

            string artist = props?.Artist?.Trim() ?? "";
            string title = props?.Title?.Trim() ?? "";
            bool hasMedia = artist.Length > 0 || title.Length > 0;
            bool playing = info?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            SetCurrent(hasMedia ? new MediaSnapshot(true, artist, title, playing) : MediaSnapshot.Empty);
        }
        catch (Exception ex)
        {
            // The session died (player closed or restarted): drop the binding and pick again
            Log.Error("Read media properties", ex);
            if (version != _version) return;
            Bind(null, null);
            SetCurrent(MediaSnapshot.Empty);
        }
    }

    private static Session? PickSession(SessionManager manager)
    {
        foreach (var s in manager.GetSessions())
        {
            string? id = SafeId(s);
            if (id != null && id.IndexOf("Spotify", StringComparison.OrdinalIgnoreCase) >= 0)
                return s;
        }
        return manager.GetCurrentSession();
    }

    private static string? SafeId(Session? s)
    {
        if (s == null) return null;
        try { return s.SourceAppUserModelId; }
        catch { return null; }
    }

    private void Bind(Session? session, string? id)
    {
        if (_session != null)
        {
            try
            {
                _session.MediaPropertiesChanged -= OnSessionEvent;
                _session.PlaybackInfoChanged -= OnSessionEvent;
            }
            catch
            {
                // RPC to a closed player may throw: ignore
            }
        }

        _session = session;
        _sessionId = id;

        if (session != null)
        {
            try
            {
                session.MediaPropertiesChanged += OnSessionEvent;
                session.PlaybackInfoChanged += OnSessionEvent;
            }
            catch (Exception ex)
            {
                Log.Error("Subscribe session", ex);
            }
        }
    }

    private void SetCurrent(MediaSnapshot snapshot)
    {
        if (snapshot == Current) return;
        Current = snapshot;
        Changed?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Bind(null, null);
        if (_manager != null)
        {
            try
            {
                _manager.SessionsChanged -= OnManagerEvent;
                _manager.CurrentSessionChanged -= OnManagerEvent;
            }
            catch
            {
                // ignore
            }
            _manager = null;
        }
    }
}
