using System.Net;
using System.Windows;
using DynamicIsland.Core.Ingest;
using DynamicIsland.App.SystemInfo;

namespace DynamicIsland.App;

/// <summary>
/// 应用入口：启动本地事件接收服务，显示灵动岛窗口，并把后台线程上的事件
/// 经 Dispatcher 切回 UI 线程驱动药丸。同时注册非 agent 信息源（媒体/电池）。
/// </summary>
public partial class App : Application
{
    private IngestServer? _server;
    private IslandWindow? _island;
    private readonly List<IInfoSource> _sources = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _island = new IslandWindow();
        _island.Show();

        _server = new IngestServer();
        _server.EventReceived += evt =>
            Dispatcher.InvokeAsync(() => _island?.OnEvent(evt));

        // 非 agent 信息源（M4）：媒体优先于电池（注册顺序 = ActiveInfoSource 优先级）。
        // 源在后台线程触发 Changed，统一经 Dispatcher 切回 UI 线程重画。
        RegisterSource(new MediaSource());
        RegisterSource(new BatterySource());

        try
        {
            _server.Start();
        }
        catch (HttpListenerException ex)
        {
            MessageBox.Show(
                $"无法监听 127.0.0.1:7777：{ex.Message}\n\n" +
                "端口可能被占用（例如之前的 IngestProbe 仍在运行）。关掉占用进程后重启即可。",
                "DynamicIsland", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RegisterSource(IInfoSource src)
    {
        _sources.Add(src);
        src.Changed += (_, _) => Dispatcher.InvokeAsync(() => _island?.OnSourceChanged());
        _island?.AddSource(src);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        foreach (var s in _sources) s.Stop();
        _server?.Dispose();
        base.OnExit(e);
    }
}
