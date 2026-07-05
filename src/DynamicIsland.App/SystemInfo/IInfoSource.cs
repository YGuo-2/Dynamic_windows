namespace DynamicIsland.App.SystemInfo;

/// <summary>
/// 非 agent 信息源（媒体/电池等）的统一接口。
/// 实现注册到 IslandWindow；agent 全部空闲时由第一个有内容的源接管主药丸。
/// </summary>
public interface IInfoSource
{
    /// <summary>唯一标识，用于调试与日志。</summary>
    string Id { get; }
    /// <summary>横条态药丸标签文字。空字符串 = 当前无内容可显，RenderPill 跳过该源。</summary>
    string Label { get; }
    /// <summary>标签前的 Segoe Fluent Icons glyph（单色线性图标，随 Foreground 取灰阶）。空 = 不画图标。</summary>
    string Glyph { get; }
    /// <summary>开始轮询/监听。</summary>
    void Start();
    /// <summary>停止并释放资源。</summary>
    void Stop();
    /// <summary>数据变化时触发（由 App 切回 UI 线程后调用 RenderActive）。</summary>
    event EventHandler? Changed;
}
