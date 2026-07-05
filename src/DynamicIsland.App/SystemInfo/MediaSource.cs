using System.Threading.Tasks;
using Windows.Media.Control;

namespace DynamicIsland.App.SystemInfo;

/// <summary>
/// 媒体播放信息源：WinRT GlobalSystemMediaTransportControlsSessionManager。
/// 监听 SessionsChanged / MediaPropertiesChanged / PlaybackInfoChanged；
/// 暂停或无播放时 Label 返回空（不接管药丸）。
/// </summary>
public sealed class MediaSource : IInfoSource
{
    // Segoe Fluent Icons: MusicNote (EC4F) — char 转换避免 PUA 在源码传输中被吞
    private static readonly string MusicNoteGlyph = ((char)0xEC4F).ToString();

    public string Id => "media";
    public string Label => _label;
    public string Glyph => MusicNoteGlyph;
    public event EventHandler? Changed;

    private string _label = "";
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;

    public void Start() => Task.Run(InitAsync);

    public void Stop()
    {
        UnsubscribeSession(_session);
        if (_manager != null) _manager.SessionsChanged -= OnSessionsChanged;
        _manager = null;
        _session = null;
        _label = "";
    }

    private async Task InitAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.SessionsChanged += OnSessionsChanged;
            await AttachCurrentSession();
        }
        catch { /* WinRT 不可用（极罕见）：静默降级 */ }
    }

    private async Task AttachCurrentSession()
    {
        UnsubscribeSession(_session);
        _session = _manager?.GetCurrentSession();

        if (_session == null) { SetLabel(""); return; }

        _session.MediaPropertiesChanged += OnPropertiesChanged;
        _session.PlaybackInfoChanged += OnPlaybackChanged;
        await RefreshLabel();
    }

    private void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager s,
        SessionsChangedEventArgs _) => Task.Run(AttachCurrentSession);

    private void OnPropertiesChanged(GlobalSystemMediaTransportControlsSession s,
        MediaPropertiesChangedEventArgs _) => Task.Run(RefreshLabel);

    private void OnPlaybackChanged(GlobalSystemMediaTransportControlsSession s,
        PlaybackInfoChangedEventArgs _) => Task.Run(RefreshLabel);

    private async Task RefreshLabel()
    {
        try
        {
            var ses = _session;
            if (ses == null) { SetLabel(""); return; }

            var playback = ses.GetPlaybackInfo();
            if (playback?.PlaybackStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            { SetLabel(""); return; }

            var props = await ses.TryGetMediaPropertiesAsync();
            string title = props?.Title ?? "";
            if (title.Length > 24) title = title[..22] + "…"; // 超长截断（省略号 U+2026，非 PUA）
            SetLabel(title);
        }
        catch { SetLabel(""); }
    }

    private void SetLabel(string label)
    {
        if (label == _label) return;
        _label = label;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void UnsubscribeSession(GlobalSystemMediaTransportControlsSession? s)
    {
        if (s == null) return;
        s.MediaPropertiesChanged -= OnPropertiesChanged;
        s.PlaybackInfoChanged -= OnPlaybackChanged;
    }
}
