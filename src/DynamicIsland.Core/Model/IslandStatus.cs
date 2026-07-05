namespace DynamicIsland.Core.Model;

/// <summary>灵动岛对外呈现的状态（M1 子集，后续在 Core 里扩展为完整状态机）。</summary>
public enum IslandStatus
{
    Idle,            // 空闲：药丸最小、暗淡
    Thinking,        // 思考中：呼吸光点
    RunningTool,     // 执行工具：显示工具名
    WaitingApproval, // 等待批准：高亮提醒
    Done,            // 完成：✓，数秒后回 Idle
    Ambient          // 非 agent 信息源（媒体/电池）在空闲时接管药丸显示；不参与 agent 优先级权重
}
