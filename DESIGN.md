# Windows 灵动岛 —— 设计文档

> 代号：`Dynamic_windows`（暂定，可改）
> 文档版本：v0.8 · 2026-06-19
> 变更：v0.8 按用户审美把详情卡重整为 **全部居中 + 分组背景块**（`TaskCard` 任务清单组 / `MetaCard` 元信息组两个浅色圆角块，内容全居中，无 todo 时任务块整体隐藏只剩元信息块）；两道裁剪上限随背景块 padding + 卡间距上调到 Window 340 / Grid 300。端到端三态截图签收。详见 §9.1。
> 变更：v0.7 修复真实会话 todo 清单为空：当前 Claude Code 使用 `TaskCreate` / `TaskUpdate` 任务系统而非 `TodoWrite`，详情卡改为从 transcript 全量重建任务清单与进度，`TodoWrite` 仅保留兼容路径。详见 §9.1。
> 变更：v0.6 修复两个状态显示 bug——① **工具态延迟回落**（毫秒级工具 Pre/Post 同秒致「⚙ 工具名」一闪而过、视觉上一直「思考中」→ Post 后保留 ~1.2s 再回落，连续工具无缝衔接）；② **idle 展开圆点居中**（去掉「空闲」文字让状态圆点单独居中）。详见 §9.1。
> 变更：v0.5 详情卡 todo 由单行当前任务升级为 **多行清单 + 进度（完成项折叠计数）**，并根治「UserPromptSubmit 清空致清单常不显示」（详见 §9.1）。
> 变更：v0.4 记录 **Claude 端到端实测签收**——真实 hooks 驱动 + transcript 读数 + UI 渲染全绿，并修复一处 transcript 尾窗读空竞态（详见 §9.1）。
> 变更：v0.3 记录 M1 / M2 落地与一处关键调整——详情卡的「模型 / 已用上下文」改从 **transcript(JSONL)** 读取，不再依赖 statusLine（避免碰全局配置 / 破坏用户 ccstatusline）。进度详见 §9.1。
> 变更：v0.2 修正 Codex —— 由 `notify` 升级为与 Claude Code 对等的 **Hooks**（stdin JSON）机制。

---

## 0. 决策摘要

| 维度 | 决定 | 状态 |
|---|---|---|
| 核心定位 | 把终端 AI Agent（Claude Code / Codex）的运行状态可视化到屏幕顶部的悬浮"灵动岛" | ✅ 已定 |
| 语言 / 运行时 | C# / .NET 8+（推荐当前 LTS） | ✅ 已定 |
| UI 框架 | **WPF**（全自绘悬浮窗 + Storyboard 动画） | ✅ 已定 |
| 信息采集 | Claude 与 Codex **均用 Hooks**（stdin JSON）；Claude 额外用 statusLine 取成本/token；Codex 备选 notify | ✅ 已定 |
| 传输方式 | 采集脚本/命令 → 本地 HTTP（`127.0.0.1`，仅 loopback） | ✅ 已定 |
| 架构分层 | 采集层 / 传输层 / 应用层（Core 类库 + WPF 壳） | ✅ 已定 |

---

## 1. 项目概述与目标

### 1.1 一句话

在 Windows 屏幕顶部中央做一颗仿 iOS 灵动岛的悬浮"黑药丸"，**实时显示 Claude Code 和 Codex 正在做什么**——在思考、正在调用什么工具、是否在等你批准、跑完了没、花了多少 token / 钱。

### 1.2 与 iPhone 灵动岛的本质区别

iPhone 的灵动岛是**变废为宝**（利用挖孔区域）；Windows 显示器顶部没有挖孔，所以它是一个**纯新增的悬浮 UI 元素**。这带来一条核心设计约束：

> 它会占用屏幕顶部空间，因此**默认必须极小 / 半隐藏，有活动时才膨胀展开**，否则会碍事。

### 1.3 核心价值

终端里的 AI Agent 状态藏在某个窗口里，容易被遮挡、容易错过"等你批准"的时刻。把状态抽出来浮在屏幕顶部，**一眼可见、不抢占前台**，并天然契合灵动岛"同时展示多个活动"的形态——每个 Agent 会话 = 一颗岛。

### 1.4 目标用户

重度使用 Claude Code / Codex 的开发者（首位用户：作者本人）。

---

## 2. 设计原则与非目标

### 2.1 设计原则

1. **像灵动岛**：默认极小、有事才膨胀、紧凑↔展开之间是流畅的"液态"形变，而非生硬弹窗。
2. **不打扰**：永远置顶但**不抢焦点**，空白处鼠标可穿透，全屏应用/游戏时自动隐藏。
3. **低占用**：常驻 7×24，空闲时内存几十 MB、CPU ≈ 0。
4. **多活动**：同时存在多个 Agent 会话时，并排/堆叠显示多颗岛。
5. **可扩展**：信息源是插件式的——今天接 Claude Code / Codex，明天能接媒体播放、电池、下载、CI 等。
6. **采集与展示解耦**：采集端只负责"把事件 POST 出来"，所有逻辑在主程序里，换信息源不动 UI。

### 2.2 非目标（至少初期不做）

- ❌ 不做移动端、不跨平台（聚焦 Windows）。
- ❌ 初期不做花哨小组件（文件托盘、剪贴板等），先把 Agent 状态做扎实。
- ❌ 不去解析终端文本输出（脆弱、易碎）——只用官方事件钩子。
- ❌ 不替代终端，不接管输入；只做**只读的状态展示**（"等待批准"也只是提醒，批准动作仍在终端里完成）。

---

## 3. 总体架构

```
┌─────────────────────────────────────────────────────────────────┐
│  采集层 (Ingest sources)                                          │
│                                                                   │
│   Claude Code ──hooks(stdin)──┐                                   │
│                 statusLine ───┤                                   │
│                               ├──► curl -X POST                   │
│   Codex ───────hooks(stdin)───┤                                   │
│                (notify 备选)──┘                                   │
│                               ▼                                   │
└───────────────────────── HTTP POST ──────────────────────────────┘
                                │  http://127.0.0.1:7777
                                ▼
┌─────────────────────────────────────────────────────────────────┐
│  DynamicIsland.Core (类库 / 框架无关 / 可单元测试)                  │
│                                                                   │
│   HttpListener ─► Normalizer ─► StateMachine ─► SessionStore      │
│   (接收/路由)     (各源→统一事件)  (状态转移/超时)   (按 sessionId)  │
│                                              │                    │
└──────────────────────────────────────── 状态变更事件 ─────────────┘
                                                │
                                                ▼
┌─────────────────────────────────────────────────────────────────┐
│  DynamicIsland.App (WPF)                                          │
│                                                                   │
│   IslandWindow(透明/置顶/穿透/不抢焦点)                            │
│     └─ 每个会话一颗 Island 视图：紧凑态 ↔ 展开态（Storyboard 形变） │
│   SystemInfo(可选：媒体/电池 via WinRT)                            │
└─────────────────────────────────────────────────────────────────┘
```

**关键架构决定**：业务逻辑全部放进 `DynamicIsland.Core` 类库（不引用 WPF），UI 层做薄。好处：状态机可单元测试、未来换 UI 或加信息源不动核心。

---

## 4. 信息采集层

两个 Agent 都通过 **stdin 收 JSON 的 Hooks** 上报状态，事件类型和字段命名（`session_id`/`hook_event_name`/`cwd`…）高度一致，因此**采集配置和归一化逻辑可基本共用**。主要差异只有两点：Claude Code 额外有 statusLine 提供实时成本/token；Codex 的 hook `command` 是 argv 数组且 Windows 要用 `commandWindows`。

### 4.1 Claude Code

两条通道配合：**Hooks** 驱动状态变化，**statusLine** 提供实时数据。

#### 4.1.1 Hooks —— 状态机的事件源

配置在 `~/.claude/settings.json`，用 `type: "command"`，把 stdin 的事件 JSON 直接 POST 给主程序：

```json
{
  "hooks": {
    "UserPromptSubmit": [{ "hooks": [{ "type": "command",
      "command": "curl -s -X POST http://127.0.0.1:7777/claude --data-binary @-" }] }],
    "PreToolUse":  [{ "hooks": [{ "type": "command",
      "command": "curl -s -X POST http://127.0.0.1:7777/claude --data-binary @-" }] }],
    "PostToolUse": [{ "hooks": [{ "type": "command",
      "command": "curl -s -X POST http://127.0.0.1:7777/claude --data-binary @-" }] }],
    "Notification":[{ "hooks": [{ "type": "command",
      "command": "curl -s -X POST http://127.0.0.1:7777/claude --data-binary @-" }] }],
    "Stop": [{ "hooks": [{ "type": "command",
      "command": "curl -s -X POST http://127.0.0.1:7777/claude --data-binary @-" }] }]
  }
}
```

核心事件与含义：

| 事件 | 触发时机 | → 灵动岛状态 | 关键字段 |
|---|---|---|---|
| `UserPromptSubmit` | 用户提交提问 | **Thinking** | `prompt` |
| `PreToolUse` | 工具调用前 | **RunningTool: <工具名>** | `tool_name`, `tool_input` |
| `PostToolUse` | 工具调用完成 | 回到 **Thinking** | `tool_name` |
| `Notification` | 需要权限/空闲提示 | **⚠️ WaitingApproval** | `notification_type` |
| `Stop` | 一轮回答结束 | **Done** | — |
| `SessionStart` / `SessionEnd` | 会话开始/结束 | 创建/销毁岛 | `session_id`, `cwd` |

所有事件都带通用字段：`session_id`、`cwd`、`hook_event_name`、`transcript_path`。用 `session_id` 区分该点亮哪一颗岛。

> 注：上表为确认稳定的核心事件；Claude Code 还有 `SubagentStop`、`PreCompact` 等，按需接入。各事件具体字段以官方文档为准：<https://code.claude.com/docs/en/hooks>

#### 4.1.2 statusLine —— 实时数据源

配置在同一个 `settings.json`。statusLine 本是给状态栏生成文字用的，会在每次新回复/压缩/模式变化时被调用，stdin 收到一份会话 JSON，可"劫持"来获取实时数据：

```json
{
  "statusLine": { "type": "command",
    "command": "curl -s -X POST http://127.0.0.1:7777/claude/status --data-binary @- ; echo ' '" }
}
```

可取字段（节选，以官方文档为准 <https://code.claude.com/docs/en/statusline>）：

| 字段 | 含义 |
|---|---|
| `model.display_name` / `model.id` | 当前模型 |
| `workspace.current_dir` / `cwd` | 工作目录 |
| `cost.total_cost_usd` | 累计花费（美元） |
| `context_window.used_percentage` | 上下文占用百分比 |
| `context_window.total_input_tokens` / `total_output_tokens` | token 数 |
| `session_id` | 会话标识（与 hooks 对齐） |

> ⚠️ statusLine 的 stdout 会被当作状态栏文字显示，所以转发命令末尾补一个 `echo ' '`，避免状态栏显示 curl 的输出。

### 4.2 Codex（Hooks，与 Claude Code 基本对等）

Codex 早已支持完整的生命周期 **Hooks**，机制与 Claude Code 高度一致：**同样从 stdin 收 JSON**，事件类型与字段命名也大量重合。所以 Codex 侧能拿到与 Claude Code 同等粒度的中间态（思考 / 工具调用 / 等待批准 / 完成），不再是"只能通知一下完成"。

**配置**：`~/.codex/config.toml` 内联 `[hooks]`（或 `~/.codex/hooks.json`，二选一勿混用），需先开 `features.hooks`。Codex 的 `command` 是 **argv 数组**，Windows 用 `commandWindows` 覆盖（`curl.exe` 系统自带）：

```toml
features.hooks = true

[[hooks.UserPromptSubmit]]
matcher = "*"                      # regex；"" 或 "*" = 匹配全部
[[hooks.UserPromptSubmit.hooks]]
type = "command"
command        = ["curl","-s","-X","POST","http://127.0.0.1:7777/codex","--data-binary","@-"]
commandWindows = ["curl.exe","-s","-X","POST","http://127.0.0.1:7777/codex","--data-binary","@-"]

# PreToolUse / PostToolUse / PermissionRequest / Stop 照抄，仅改事件名
```

支持的事件：`PreToolUse`、`PostToolUse`、`PermissionRequest`、`UserPromptSubmit`、`Stop`、`SessionStart`、`SubagentStart`、`SubagentStop`、`PreCompact`、`PostCompact`。

stdin base payload（命名与 Claude Code 一致，迁移成本极低）：

```jsonc
{ "session_id":"...", "hook_event_name":"PreToolUse", "cwd":"...",
  "model":"...", "transcript_path":"...|null",
  "turn_id":"...",             // turn 范围事件附带
  "permission_mode":"default"  // default/acceptEdits/plan/dontAsk/bypassPermissions
}
```

**三个 Codex 专属注意点：**
1. ✅ **Windows 可用性（已实测确认）**：hooks 需开 `features.hooks`。官方早期文档曾标注不支持 Windows，但**作者已在 Windows 上实测跑通过 Codex hooks**——故 Codex 主路径直接定为 hooks，`notify` 仅作兜底。
2. ⚠️ **`Stop` 事件必须输出合法 JSON**（纯文本无效）。最简解：让接收端对所有 `/codex` 请求一律返回 `{}`——这样连 Stop 都能直接用 curl，无需额外脚本。
3. ⚠️ **信任模型**：非托管 hooks 需被信任后才运行（用户级 hooks 不受项目信任限制；项目级需 `.codex/` 被信任；自动化可用 `--dangerously-bypass-hook-trust`）。

**数据局限**：Codex hook 的 base payload 有 `model`，但没有 Claude statusLine 那样的实时 **cost / context 百分比**；Codex 侧的花费/token 暂无稳定数据源（transcript 格式官方声明不保证稳定，不宜依赖）。

**备选 `notify`**：万一某版本 hooks 在 Windows 不可用，可退回 `notify`（事件 JSON 作为 **argv 最后一个参数**，目前仅 `agent-turn-complete`），用 `hooks/island-notify.py` 转发，至少保住"完成 / 等待"提示。

> 来源：Hooks 指南 <https://developers.openai.com/codex/hooks>、配置参考 <https://developers.openai.com/codex/config-reference>、高级配置 <https://developers.openai.com/codex/config-advanced>。

### 4.3 统一事件格式（归一化）

采集层各源进入 Core 后**归一化为统一内部事件**，下游只认这一种格式：

```jsonc
{
  "source":    "claude" | "codex",
  "sessionId": "abc123",
  "type":      "prompt" | "tool_start" | "tool_end" | "waiting" | "done" | "status",
  "tool":      "Bash",                 // 仅 tool_* 事件
  "model":     "Opus",
  "cwd":       "E:/CodeProject/...",
  "costUsd":   0.0123,                 // 仅 Claude statusLine
  "contextPct": 8,                     // 仅 Claude statusLine
  "message":   "...",
  "ts":        1718700000
}
```

映射关系（两源 hooks 高度对称）：

| 源事件 | → 统一 `type` |
|---|---|
| Claude `UserPromptSubmit` / Codex `UserPromptSubmit` | `prompt` |
| Claude `PreToolUse` / Codex `PreToolUse` | `tool_start` |
| Claude `PostToolUse` / Codex `PostToolUse` | `tool_end` |
| Claude `Notification`(permission) / Codex `PermissionRequest` | `waiting` |
| Claude `Stop` / Codex `Stop` | `done` |
| Claude statusLine | `status`（Codex 无对应；其 `model` 可从 hook base payload 取） |
| Codex `agent-turn-complete`（notify 备选） | `done` |

---

## 5. 内部数据模型与状态机

### 5.1 会话状态模型

```csharp
public enum AgentSource { ClaudeCode, Codex }

public enum AgentState {
    Idle,             // 空闲：药丸最小 / 半隐藏
    Thinking,         // 思考中：呼吸光效
    RunningTool,      // 执行工具：显示工具名
    WaitingApproval,  // 等待批准：⚠️ 高亮膨胀
    Done,             // 完成：✓ + 摘要，数秒后回 Idle
    Error
}

public class AgentSession {
    public string SessionId { get; set; }
    public AgentSource Source { get; set; }
    public AgentState State { get; set; } = AgentState.Idle;
    public string CurrentTool { get; set; }
    public string Cwd { get; set; }
    public string Model { get; set; }
    public double CostUsd { get; set; }
    public int ContextPercent { get; set; }
    public string LastMessage { get; set; }
    public DateTime LastUpdate { get; set; }
}
```

`SessionStore` 按 `SessionId` 维护一个 `AgentSession` 字典；每颗岛绑定一个会话。

### 5.2 状态机

```
                    ┌────────────────────────────────────────┐
                    ▼                                         │
   ┌──────┐ prompt ┌──────────┐ tool_start ┌─────────────┐   │
   │ Idle │───────►│ Thinking │───────────►│ RunningTool │   │
   └──────┘        └──────────┘◄───────────└─────────────┘   │
      ▲                │  ▲         tool_end                  │
      │           done │  │ waiting                           │
      │ (超时回收)      ▼  │                                   │
      │            ┌──────┴────────────┐                      │
      │            │ WaitingApproval ⚠️ │──── (用户在终端批准) ─┘
      │            └───────────────────┘     后续事件继续推进
      │                │ done
      │                ▼
      │            ┌──────┐  N 秒后
      └────────────│ Done │──────────► Idle
                   └──────┘
```

规则：
- 任意状态收到 `waiting` → 进 `WaitingApproval`（最高优先级，必定膨胀提醒）。
- `Done` 停留 N 秒（默认 5s）展示摘要后自动回 `Idle`。
- `Idle` 持续超过 M 分钟（或收到会话结束）→ 回收该岛。
- `RunningTool` 收到 `tool_end`（`PostToolUse`）**不立即**回 `Thinking`，而是保留工具名 ~1.2s 再回落；其间来新 `tool_start` 即无缝切换。否则毫秒级工具（Pre/Post 同秒）的工具名会一闪而过、视觉上一直停在 `Thinking`。
- Claude Code 与 Codex（启用 hooks 后）**同等驱动完整状态机**；若 Codex 仅 `notify` 可用，则退化为只在 `done` 点亮一下。

---

## 6. 应用层与 UI 设计（WPF）

### 6.1 窗口形态（灵动岛的"地基"）

一个 `IslandWindow`，要求：透明背景、异形（圆角药丸）、永远置顶、不抢焦点、空白处点击穿透、不在任务栏 / Alt-Tab 中出现。

**WPF 侧（开箱即用部分）：**

```xml
<Window WindowStyle="None"
        AllowsTransparency="True"
        Background="Transparent"
        Topmost="True"
        ShowInTaskbar="False"
        ResizeMode="NoResize">
```

**Win32 interop 部分**（在 `SourceInitialized` 里给窗口加扩展样式）：

| 样式 | 值 | 作用 |
|---|---|---|
| `WS_EX_NOACTIVATE` | `0x08000000` | 点击不抢焦点 |
| `WS_EX_TOOLWINDOW` | `0x00000080` | 不进任务栏 / Alt-Tab |
| `WS_EX_TRANSPARENT` | `0x00000020` | 鼠标穿透（**动态切换**：紧凑态穿透，交互态关闭） |

```csharp
// 伪代码：SourceInitialized 时
var hwnd = new WindowInteropHelper(this).Handle;
int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
```

**点击穿透策略**：紧凑/空闲态整窗设 `WS_EX_TRANSPARENT` 全穿透，用低层鼠标钩子检测指针进入顶部热区时"唤醒"并移除该样式使其可交互；移开后恢复。（细节属 M5 精修，见 §10。）

**定位**：主显示器工作区顶部水平居中；监听显示器变更与 DPI 变化重新定位（见 §10 风险）。

### 6.2 视觉状态

| 状态 | 紧凑态外观 | 展开态外观（hover / 重要事件） |
|---|---|---|
| Idle | 极小的暗色药丸 / 几乎隐形 | — |
| Thinking | 药丸内一个呼吸光点 | 模型名 + "思考中…" |
| RunningTool | 工具图标 + 简短名（如 `⚙ Bash`） | 工具名 + 参数摘要（命令行/文件名） |
| WaitingApproval | ⚠️ 药丸变色 + 脉冲，**自动膨胀** | "等待批准：<操作>"，引导去终端处理 |
| Done | ✓ | 本轮摘要：耗时 / token / 花费；数秒后收起 |

多会话：多颗药丸沿顶部水平排列或纵向堆叠；`WaitingApproval` 的岛优先靠中、最显眼。

> **状态圆点居中规则**：展开详情卡顶部沿用横条态的「圆点 + 状态文字」——有文字时圆点+文字**一组居中**，idle 无文字时**圆点单独居中**。
> **工具态延迟回落**：`RunningTool` 在 `PostToolUse` 后保留 ~1.2s 再回 `Thinking`，连续工具无缝衔接（避免毫秒级工具 Pre/Post 同秒导致工具名一闪而过）。

### 6.3 动画（质感的灵魂）

- 紧凑 ↔ 展开：对宽 / 高 / 圆角做 `DoubleAnimation`，配弹性缓动（`BackEase` / `ElasticEase` / `CubicEase`）模拟"液态"膨胀。
- 状态切换：颜色、透明度、内容交叉淡入淡出。
- 呼吸 / 脉冲：循环 `Storyboard`。
- 渲染由 WPF 走 DirectX 硬件加速，CPU 负载极低。
- 若 Storyboard 不足以表达更高级的形变质感，备选 **SkiaSharp 自绘** 或 `CompositionTarget.Rendering` 逐帧。

### 6.4 交互

- **Hover**：指针移到顶部热区 → 对应岛展开。
- **点击**：展开态点击 → 预留动作（如把对应终端窗口带到前台 / 复制 cwd；后续定义）。
- **不接管输入**：批准等操作仍在终端完成，岛只提醒。

---

## 7. 技术选型与依赖

| 关注点 | 选择 | 说明 |
|---|---|---|
| 运行时 | .NET 8+（推荐当前 LTS） | TFM 示例 `net9.0-windows10.0.19041.0`（带 Windows SDK 以调用 WinRT） |
| UI | WPF | 透明异形置顶悬浮窗开箱即用；全自绘，自带控件的"老"不影响观感 |
| HTTP 接收 | `System.Net.HttpListener` | 内置、轻量，仅绑定 `127.0.0.1`，无需 ASP.NET |
| JSON | `System.Text.Json` | 内置高性能 |
| 系统信息（可选） | WinRT `Windows.Media.Control` / 电池 API | WPF 通过 Windows SDK TFM 可直接调用 |
| 动画进阶（可选） | SkiaSharp | 需要更复杂自绘形变时 |

---

## 8. 模块划分与项目结构

```
Dynamic_windows/
├─ DynamicIsland.sln
├─ DESIGN.md
├─ src/
│  ├─ DynamicIsland.Core/          # 类库（不引用 WPF，可单测）
│  │  ├─ Ingest/   HttpServer.cs           # HttpListener + 路由 /claude /codex
│  │  ├─ Normalize/ ClaudeNormalizer.cs    # 各源 JSON → 统一事件
│  │  │             CodexNormalizer.cs
│  │  ├─ Model/    AgentSession.cs / AgentState.cs / UnifiedEvent.cs
│  │  └─ State/    SessionStore.cs / StateMachine.cs   # 状态转移 + 超时回收
│  │
│  ├─ DynamicIsland.App/           # WPF 主程序（薄壳）
│  │  ├─ App.xaml(.cs)
│  │  ├─ IslandWindow.xaml(.cs)            # 悬浮窗 + Win32 interop
│  │  ├─ Views/    CompactView / ExpandedView / 各状态模板
│  │  ├─ Animation/ IslandAnimations.cs
│  │  └─ SystemInfo/ MediaProvider.cs       # 可选
│  │
│  └─ DynamicIsland.Setup/         # 安装 / 配置注入
│     └─ ConfigInstaller.cs                # 自动写入 hooks/notify、开机自启
└─ hooks/
   └─ island-notify.py             # Codex notify 备选转发脚本
```

---

## 9. 里程碑路线图

| 里程碑 | 目标 | 状态 |
|---|---|---|
| **M0 数据流贯通** | `HttpListener` 起服务 + 配置 Claude hooks 转发 + 打印收到的事件 | ✅ 完成 |
| **M1 静态药丸** | WPF 透明 / 置顶 / 不抢焦点窗 + 顶部居中药丸 | ✅ 完成 |
| **M2 单会话状态机 + 动画** | 事件驱动状态机 + 液态形变 + hover 详情卡（当前任务 / 目录 / 模型 / 强度 / 上下文，按 `session_id` 分桶） | ✅ 完成（详见 §9.1） |
| **M3 多会话（Claude + Codex）** | 多 `sessionId` → 多颗岛；Codex 走 hooks（含 `PermissionRequest`→等待批准）；抽 `DynamicIsland.Core` | ⬜ 未开始 |
| **M4 系统信息扩展** | 接媒体播放 / 电池（WinRT），验证"多活动"形态 | ⬜ 未开始 |
| **M5 打磨** | 设置面板、开机自启、安装器自动注入配置、多显示器 / DPI、点击穿透精修、全屏自动隐藏 | ⬜ 未开始 |

> 建议**先做 M0**：它最小、最能消除"信息拿不拿得到"的不确定性，跑通后整个项目心里就有底了。

### 9.1 当前实现状态（2026-06-19）

**已完成（M0 + M1 + M2）**
- 本地 HTTP 接收端 `IngestServer`（`127.0.0.1:7777`），对所有请求回 `{}`（兼容 Codex Stop）；事件追加 `%TEMP%/dynamicisland-events.log` 便于"不靠肉眼"核验。
- 灵动岛悬浮窗：透明 / 置顶 / 不抢焦点（`WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`）；横条态状态机（Thinking / RunningTool / WaitingApproval / Done）+ 液态形变动画；hover 展开详情卡。
- 事件按 `session_id` 分桶（`Dictionary<string, Session>`），活跃会话驱动 UI，多项目并发不串台。
- 详情卡：内容**全部居中、分两个浅色圆角背景块**——`TaskCard` 任务清单组（**进度 + 多行清单**：完成项折叠成计数 `📋 已完成 X/Y`，只铺开在做 `▶` / 待办 `○`，超 6 条折叠「… 还有 N 条」，无 todo 时整块隐藏）+ `MetaCard` 元信息组（**目录 / 模型 / 思考强度 / 已用上下文**）；高度按未完成行数自适应（含两块 padding + 卡间距）。

**相对本文档前几节的关键调整**
- **数据来源去 statusLine 化**：模型、已用上下文原计划走 statusLine（§4.1.2），实际改为读 hook 自带的 `transcript_path` 指向的 **transcript(JSONL)** 末尾 assistant 记录（`message.model` + `message.usage`）。原因：statusLine 全局只有一个槽，会与用户的 ccstatusline 冲突；走 transcript 则**零碰全局配置、对所有用户通用**。**故 §4.1.2 的 statusLine 通道当前不启用。**
- **思考强度** ← hook event 的 `effort.level`（PreToolUse / PostToolUse / Stop / SubagentStop）。
- **todo 清单 + 进度** ← transcript 全量扫描 `TaskCreate` / `TaskUpdate` 历史重建：`TaskCreate` 的 id 从配对 `tool_result` 文本 `Task #N created...` 提取，内容取 `subject / activeForm`；`TaskUpdate` 按 `taskId/status` 更新，`deleted` 跳过。`TodoWrite` 的 `tool_input.todos` 全量路径仍保留作兼容。详情卡按「完成项折叠成计数 + 只铺开在做 / 待办」渲染，在做项取 activeForm（蓝、加粗）。
- **已用上下文只显示 token 数、不显示百分比**：transcript 的 model id 不带 `[1m]`，无法可靠区分 200k / 1M 窗口，百分比会误导。
- **项目结构**：当前仍是单一 `DynamicIsland.App`（§8 的 `DynamicIsland.Core` 抽取推迟到 M3）。

**待办 / 已知问题**
- ✅ **详情卡视觉重整：全部居中 + 分组背景块（2026-06-19）**：按用户审美（带 ASCII 预览的取舍）把详情面板从「居中 / 左对齐混排、四类信息无分隔」改为**内容全居中 + 两个浅色圆角背景块**（`TaskCard` 任务清单组、`MetaCard` 元信息组）。todo 行加 `TextAlignment / HorizontalAlignment = Center` + `MaxWidth = 208`（短居中、长省略）；`RefreshDetail` 在 `total == 0` 时整块隐藏 `TaskCard`。背景块 padding（上下 8）+ 卡间距（8）撑高 → 两道裁剪上限从 280 / 232 上调到 **Window 340 / Grid 300**，`DetailTargetHeight` 公式计入两块 padding + 卡间距（最坏 7 行 = 273 < 300）。端到端三态截图签收：work（真实中文清单 + 居中 + 分区）/ empty（仅元信息块）/ 7 行最高情况（不裁底）。
- ✅ **状态显示两个 bug 修复（2026-06-19）**：① **工具态延迟回落**——真实会话里 Read/Edit/Write 多为同秒 `PreToolUse`+`PostToolUse`（毫秒级），原 `PostToolUse → Thinking` 把「⚙ 工具名」瞬间盖掉、视觉上一直「思考中」；改为 Post 后启动 ~1.2s `_toolHoldTimer`，期间来新工具即无缝切换，真正停顿超 1.2s 才回「思考中」。② **idle 圆点居中**——hover 展开时 idle 也写「空闲」文字，`TitleRow` 的圆点+文字一组居中致圆点偏左；改为 idle 隐藏 `Label`，圆点单独居中（工作态有文字仍一组居中）。端到端截图核对：工具名稳定显示 + 连续切换 + 1.2s 回落 + idle 圆点正中，全绿。
- ✅ **Claude 端到端已实测打通（2026-06-19）**：本仓库 Claude Code 环境**会执行项目级 shell hooks**——当前会话自身工具调用即真实驱动 App（`effort`/`session_id`/工具名逐一对上），`claude -p` headless 独立会话（全新 UUID `e1d41f51…`）亦照常上报；transcript 读数（模型/上下文）、任务系统清单重建、UI 渲染（截图见橙色「等待批准」态 + 目录行 `📁 .../CodeProject/Dynamic_windows`）逐一核对通过。**过程中发现并修复一处 transcript 读数竞态**：末尾出现接近/超过 256KB 的超大记录（图片 base64 / 整文件读取）正在写入时，固定 256KB 尾窗读空 → `TranscriptReader.ReadLatest` 改为 4× 扩窗回退兜底。注：hooks 会话启动时加载，中途改 settings / 调 effort 仍需新开会话生效。
- ℹ️ **多屏 / 高 DPI 定位**：`Reposition` 用主屏 `WorkArea` 顶部居中，单屏（实测 2560@150%）居中正常；多显示器精细适配归入 M5。（早先「定位偏右」实为 PowerShell 非 DPI-aware 的截图坐标错觉，已纠正。）
- **Codex 接入**：hooks 在 Windows exec 下未送达，待查（§4.2）。
- 待修：`setup/codex-config-snippet.toml` 的 Codex `command` 数组 → 字符串形式。

---

## 10. 风险与未决问题

| # | 风险 / 未决 | 应对 |
|---|---|---|
| 1 | **hook 在 Windows 下的执行环境**：Claude Code 用什么 shell 跑 `command`、`curl.exe` 是否在 PATH、引号/转义是否正确 | M0 第一件事就验证；必要时改为调用一个 `.cmd`/`.ps1` 包装脚本 |
| 2 | **Codex hooks 配置项**：需 `features.hooks` 启用并通过信任模型（Windows 可用性已由作者实测确认） | 注意 `Stop` 须输出合法 JSON——接收端对 `/codex` 统一回 `{}` 即可规避 |
| 3 | **点击穿透 + 局部可交互** 的动态切换实现 | 低层鼠标钩子 + 动态增删 `WS_EX_TRANSPARENT`；列入 M5 |
| 4 | **多显示器 + 每显示器 DPI 缩放** 的定位 | 监听 `DisplaySettingsChanged` / DPI 变更重新定位；M5 |
| 5 | **全屏应用 / 游戏时遮挡** | 检测前台窗口是否全屏，自动隐藏；M5 |
| 6 | **端口冲突 / 安全** | 固定端口可配置；只绑定 `127.0.0.1`，不监听外网 |
| 7 | **statusLine stdout 契约** | 转发命令补 `echo ' '`，避免状态栏显示杂乱输出 |
| 8 | **Codex 侧无实时 cost/token** | base payload 仅有 `model`；花费/token 暂不展示，或仅展示 Claude 侧 |
| 9 | **Done→Idle 超时与会话回收** 的时长 | 设为可配置（默认 Done 5s、会话空闲回收若干分钟） |

---

## 11. 未来扩展

把"信息源"做成插件式之后，这颗岛可以容纳远不止 Agent 状态：

- 🎵 媒体播放（歌名 / 封面 / 控制）
- 🔋 电池 / 充电、🎧 蓝牙耳机电量
- ⬇️ 下载进度、🏗️ CI / 构建状态、📅 日历提醒、🍅 番茄钟

最终形态：**Windows 上的通用"灵动岛平台"**，Agent 状态只是它的第一个、也是最有特色的信息源。

---

*下一步建议：从 M0 开始——先把本地 HTTP 接收端跑起来，配好 Claude / Codex hooks，亲眼看到事件流出来，再进入 UI。*
