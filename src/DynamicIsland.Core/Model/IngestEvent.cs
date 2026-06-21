namespace DynamicIsland.Core.Model;

/// <summary>
/// 采集层归一化后的一条事件（最小字段）。两个 Agent 的 hooks 字段命名高度一致，
/// 故同一结构即可承载 Claude 与 Codex。
/// </summary>
/// <param name="Source">来源："claude" / "codex"。</param>
/// <param name="EventName">原始 hook 事件名，如 UserPromptSubmit / PreToolUse / Stop。</param>
/// <param name="Tool">工具名，仅 PreToolUse / PostToolUse 等带 tool_name 的事件有。</param>
/// <param name="SessionId">会话标识，用于区分该点亮哪一颗岛。</param>
/// <param name="Cwd">工作目录。</param>
/// <param name="Effort">思考强度（effort.level）：low/medium/high/xhigh/max。仅工具相关事件（PreToolUse/PostToolUse/Stop/SubagentStop）带。</param>
/// <param name="TranscriptPath">当前会话 transcript（JSONL）绝对路径，所有 hook 都带。读末尾 assistant 记录可取模型与上下文占用。</param>
/// <param name="CurrentTask">当前进行中的待办（TodoWrite 的 in_progress 项 activeForm）。仅 TodoWrite 的 PreToolUse 事件带。详情卡多行清单后此字段主要供日志/兜底用。</param>
/// <param name="Todos">TodoWrite 的全量待办列表（按 JSON 原序）。仅 TodoWrite 事件带；其它事件为 null。详情卡据此渲染多行清单 + 进度。</param>
/// <param name="Message">Notification 文案。区分两种语义：含 permission/approve 为工具权限请求（→ 等待批准）；"waiting for your input" 为空闲提醒（不改状态）。</param>
public record IngestEvent(
    string Source,
    string EventName,
    string? Tool,
    string? SessionId,
    string? Cwd,
    string? Effort = null,
    string? TranscriptPath = null,
    string? CurrentTask = null,
    IReadOnlyList<TodoItem>? Todos = null,
    string? Message = null);
