using System.Net;
using System.Windows;
using DynamicIsland.Core.Ingest;

namespace DynamicIsland.App;

/// <summary>
/// 应用入口：启动本地事件接收服务，显示灵动岛窗口，并把后台线程上的事件
/// 经 Dispatcher 切回 UI 线程驱动药丸。
/// </summary>
public partial class App : Application
{
    private IngestServer? _server;
    private IslandWindow? _island;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _island = new IslandWindow();
        _island.Show();

        _server = new IngestServer();
        _server.EventReceived += evt =>
            Dispatcher.InvokeAsync(() => _island?.OnEvent(evt));

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

    protected override void OnExit(ExitEventArgs e)
    {
        _server?.Dispose();
        base.OnExit(e);
    }
}
