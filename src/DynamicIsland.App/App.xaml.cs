using System.Net;
using System.Windows;
using DynamicIsland.Core.Ingest;
using DynamicIsland.App.SystemInfo;
using Forms = System.Windows.Forms;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

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
    private Forms.NotifyIcon? _trayIcon;
    private Forms.ToolStripMenuItem? _toggleIslandItem;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _island = new IslandWindow();
        _island.Show();
        CreateTrayIcon();

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

    private void CreateTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        _toggleIslandItem = new Forms.ToolStripMenuItem("隐藏灵动岛");
        _toggleIslandItem.Click += (_, _) => ToggleIslandVisibility();
        menu.Items.Add(_toggleIslandItem);
        menu.Items.Add(new Forms.ToolStripSeparator());

        var exitItem = new Forms.ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => Dispatcher.InvokeAsync(Shutdown);
        menu.Items.Add(exitItem);

        _trayIcon = new Forms.NotifyIcon
        {
            Text = "DynamicIsland",
            Icon = System.Drawing.SystemIcons.Application,
            ContextMenuStrip = menu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => ToggleIslandVisibility();
    }

    private void ToggleIslandVisibility()
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (_island is not { } island) return;

            if (island.IsVisible)
                island.Hide();
            else
            {
                island.Show();
            }

            UpdateTrayText();
        });
    }

    private void UpdateTrayText()
    {
        if (_toggleIslandItem == null || _island == null) return;
        _toggleIslandItem.Text = _island.IsVisible ? "隐藏灵动岛" : "显示灵动岛";
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.ContextMenuStrip?.Dispose();
            _trayIcon.Dispose();
            _trayIcon = null;
        }
        foreach (var s in _sources) s.Stop();
        _server?.Dispose();
        base.OnExit(e);
    }
}
