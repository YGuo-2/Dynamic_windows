# DynamicIsland

Windows 11 上模仿 iOS「灵动岛」的悬浮窗,顶部居中,实时可视化终端 AI agent(Claude Code / Codex)的状态:
思考中 / 正在跑什么工具 / 等待批准 / 完成,以及 hover 展开详情卡显示 **todo 清单 + 进度 / 目录 / 模型 / 思考强度 / 已用上下文**。

## 技术栈
- C# / .NET 10(SDK 10.0.301)、WPF、`net10.0-windows10.0.19041.0`（M4 起加 Windows SDK 版本号以启用 WinRT 媒体 API）、`WinExe`、Nullable + ImplicitUsings enable
- 本地 HTTP:`HttpListener` 监听 `127.0.0.1:7777`
- 采集:Claude Code / Codex 的 hooks(stdin JSON)经 `curl` POST 转发到该端口

## 架构 / 关键文件
两个项目:`src/DynamicIsland.Core/`(net10.0 Library,采集层,零 WPF 依赖)+ `src/DynamicIsland.App/`(net10.0-windows WinExe,WPF UI,ProjectReference 引用 Core)。**M3 已抽出 Core**(2026-06-19):采集层 5 个文件机械搬迁,namespace `DynamicIsland.App.*` → `DynamicIsland.Core.*`,App 侧仅改 3 行 using,零行为变化。解决方案 `DynamicIsland.slnx` 含 Core + App(Core 在前)。

- `Core/Ingest/IngestServer.cs` — HttpListener,收 hook JSON → `Parse` 归一化成 `IngestEvent` → 抛 `EventReceived`(**后台线程**,订阅方需自行切回 UI)。对所有请求回 `"{}"`(兼容 Codex Stop 必须输出合法 JSON)。事件追加到 `%TEMP%/dynamicisland-events.log`。
- `Core/Ingest/TranscriptReader.cs` — 读会话 transcript(JSONL)**末尾**最近一条 assistant 记录,取 `message.model` 与 `message.usage`。`FileShare.ReadWrite` 共享读(claude 进程在写),尾部回读 256KB;**找不到 usage 就 4× 扩窗回退**(`ReadWindow`)直至命中或读完整个文件。另从完整 transcript 重建 `TaskCreate` / `TaskUpdate` 任务清单,因为真实 Claude Code 会话当前不用 `TodoWrite`。
- `Core/Model/IngestEvent.cs` — 归一化事件 record(Source/EventName/Tool/SessionId/Cwd/Effort/TranscriptPath/CurrentTask/**Todos**/**Message**)。`Core/Model/TodoItem.cs` — 单条待办(Content/ActiveForm/Status,纯数据)。
- `Core/Model/IslandStatus.cs` — 状态枚举:Idle / Thinking / RunningTool / WaitingApproval / Done / **Ambient**（M4，非 agent 信息源接管空闲药丸用）。
- `IslandWindow.xaml(.cs)` — 灵动岛 UI:横条态状态机(工具态 `PostToolUse` 后延迟 ~1.2s 回落,毫秒级工具不被秒杀) + 形变动画 + hover 展开详情卡(顶部状态圆点:有文字一组居中、idle 无文字单独居中)。详情卡内容**全部居中、分两个浅色圆角背景块**(`TaskCard` 任务清单组:进度行 + `RebuildTodoList` 动态生成的居中 todo 行,完成项折叠成计数,无 todo 时整块隐藏;`MetaCard` 元信息组:目录 + 模型/强度/上下文),高度按未完成行数自适应。**多会话「主药丸 + 副会话点」**(M3,2026-06-19):按 `session_id` 分桶(`Dictionary<string, Session>`,Session 加 `Key`/`LastActivity`),`PickActive()` 按**优先级**(`Weight`:WaitingApproval4 > RunningTool3 > Thinking2 > Done1 > Idle0,同权重取最近活动)选主会话驱动主药丸,**不再是「最后一个事件」**;其余非 Idle 会话由 `RenderSatellites` 缩成 9×9 状态色小圆点,**收进药丸内部**(`Satellites` StackPanel 在 `TitleRow` 内、`Label` 之后,前置 1px 细竖分隔条 `SatSeparator`;整组 `[● 主状态文字 ┊ ● ● ●]` 靠 TitleRow `HorizontalAlignment=Center` 一同居中),药丸宽度由 `_compactWidth += SatelliteStripWidth()` 对称增长容纳副点——**不再外挂在药丸右侧单侧拖尾**(M3 UI 重做 2026-06-22:旧版 7×7 副点贴主药丸右侧独立 Margin 定位、不参与居中,破坏「灵动岛顶部居中对称」的灵魂,已重做)。超 `MaxSatellites=3` 折叠「+N」(`MakeOverflowDot`,也在岛内);hover 展开时副点整组收起(`RenderSatellites` 判 `_hovering` 隐藏 `Satellites`+`SatSeparator`,否则会挤偏居中标题),由详情卡 `SessionTabs` 接管。详情卡顶部 `SessionTabs` 列出会话标签(状态点 + 短目录,统一圆角/间距/`MaxWidth=56`,超 `MaxSessionTabs=3` 折叠「+N」且当前 pin 的会话务必在列,点击 `PinSession` 锁定查看),区分**「主药丸=自动优先级」vs「详情卡=用户 pin 想看」**(`_activeRendered`/`_displayed`/`_pinnedKey`);hover 离开清 pin 回自动,pin 的会话被 prune 自动回落。**渲染解耦三层**:`ApplyStatus(Session,...)` 只改数据、`RenderPill(Session?)` 全量重画主药丸(含 null→Idle 外观,`_compactWidth` 含副点区宽)、`RenderActive()` 调度(PickActive + 脏检查防抖,脏检查含 `_renderedSatWidth` 副点增减/变色也触发横条态重画 + RenderSatellites + 刷详情卡)。`_pruneTimer`(2s)做生命周期:Done 超 `DoneLingerSeconds=6` 回落 Idle、Idle 超 `IdleEvictSeconds=30` 移除(替代旧单例 `_doneTimer`,天然支持多会话);`_toolHoldTimer` 绑 `_toolHoldTarget` 只对当前主会话回落。**内存守护(关键)**:transcript 只给 `_displayed` 一个会话读(仅 hover 时),**绝不遍历批量拉**。**视觉走 SwiftUI 三件套**(2026-06-19):图标全用单色线性字体(Segoe Fluent Icons + Lucide 补 cpu/bar-chart),经 `AddIconRun` 以 `Inline.Run` 混排(图标字体 + 雅黑文字,颜色随 `Foreground` 得灰阶层级);展开/收起用 `SpringEase` 阻尼弹簧;背景与裁剪走 squircle 连续曲率几何,展开态圆角 `PillRadius` 14→24 动画。
- `SpringEase.cs` — 自定义 `EasingFunctionBase`,SwiftUI 同款 `Response`/`DampingFraction` 阻尼弹簧;**末端归一化校正强制 `Ease(1)=1`**,杜绝欠阻尼残留过冲让 `DoubleAnimation` 停在超尺寸值。须用 `EasingMode.EaseIn` 透传曲线。
- `Squircle.cs` / `SquircleShape.cs` — squircle 连续曲率几何:`Squircle.BuildGeometry` 用 Lamé 超椭圆(`|x|^n+|y|^n=1`,n≈5)直边 + 四角密采样多段线(clamp `r≤min(w,h)/2`);`SquircleShape : FrameworkElement` 背景层 `OnRender` 画几何、`OnRenderSizeChanged` 重画(挂 DropShadow,阴影跟随轮廓避免方形阴影);裁剪层 `ClipHost.Clip` 随 `SizeChanged` 与 `PillRadius` 重建,与背景同尺寸源不错位。
- `Assets/lucide.ttf` — Lucide 图标字体(csproj `<Resource>` 嵌入),补 Fluent 没贴切字的 cpu/bar-chart;引用走 `new FontFamily(new Uri("pack://application:,,,/"), "./Assets/#lucide")`。
- `App.xaml.cs` — 入口:起 `IngestServer`,事件经 `Dispatcher.InvokeAsync` 切回 UI 线程调 `IslandWindow.OnEvent`。M4 起另注册 `MediaSource` / `BatterySource`，`Changed` 同样切回 UI 线程调 `IslandWindow.OnSourceChanged`。
- `App/SystemInfo/IInfoSource.cs` — 非 agent 信息源接口（Id/Label/Glyph/Changed/Start/Stop），M4 两个实现：`MediaSource`（WinRT 正在播放）+ `BatterySource`（Win32 电量）。agent 全空闲时由 `ActiveInfoSource()` 选第一个有内容的源接管主药丸横条态（媒体优先于电池）；agent 活跃时信息源静默不干扰。
- `.claude/settings.json` — **项目级** hooks(5 个事件 curl 转发到 7777)。**绝不污染全局配置。**

## 数据来源(本项目最核心的知识,反复踩过坑)
详情卡四项数据 **全部走 hook 链路,零碰 statusLine / ccstatusline / 全局配置** —— 刻意为之,保证对任何用户通用(别人用什么状态栏都不破坏)。

| 字段 | 来源 |
|---|---|
| 思考强度 | hook event 的 `effort.level`(仅 `PreToolUse/PostToolUse/Stop/SubagentStop` 带;值 low/medium/high/xhigh/max) |
| 模型 | transcript 末尾 `message.model`(每条都有;**SessionStart hook 的 `model` 不可靠**,`/clear` 后会缺) |
| 已用上下文 | transcript 末尾 `message.usage` 的 `input + cache_creation + cache_read`(不含 output),同 statusLine `used_percentage` 口径 |
| todo 清单 + 进度 | transcript 全量扫描 `TaskCreate` / `TaskUpdate`: `TaskCreate` 的 id 从配对 `tool_result` 文本 `Task #N created...` 提取,内容取 `subject / activeForm`; `TaskUpdate` 按 `taskId/status` 更新。旧 `TodoWrite` 的 `tool_input.todos` 路径保留作兼容 |

- hooks 共有字段 `transcript_path` 直接给出当前会话 jsonl 路径(免扫 `~/.claude/projects`,这是 ccusage 那类工具的数据源)。
- **上下文只显示 token 数,不显示百分比**:transcript 无法可靠区分 200k / 1M 窗口(model id 不带 `[1m]`),百分比会误导。
- `TodoWrite` 不当作「动作」:不抢药丸标题,只更新 todo 清单,维持思考态;真实会话主路径是 transcript Task 重建。
- **清单跨轮保留**:`Session.Todos` 仅在收到非空 TodoWrite 或 transcript 确认存在 Task 历史时覆盖;`UserPromptSubmit` **不清空**。

## 构建 / 运行
```bash
# App 运行时锁住 exe —— 改完必须先杀进程再 build
taskkill //F //IM DynamicIsland.App.exe 2>/dev/null || true
sleep 1.5
dotnet build "E:/CodeProject/Dynamic_windows/src/DynamicIsland.App/DynamicIsland.App.csproj" -c Debug -v minimal -nologo
# 后台起 App
"E:/CodeProject/Dynamic_windows/src/DynamicIsland.App/bin/Debug/net10.0-windows/DynamicIsland.App.exe"
```
- 灌测试事件:`curl -s -X POST http://127.0.0.1:7777/claude --data-binary '<hook-json>'`(嵌套 JSON 用 `--data-binary @file` 避免转义地狱)
- `%TEMP%/dynamicisland-events.log` 是「不靠肉眼」确认事件到达 / 字段解析的日志。

### 构建发行版(Setup 安装程序)
一键打 MSI:`pwsh -File scripts/build-release.ps1` → 产物 `dist/DynamicIsland-1.0.0-win-x64.msi`(~51MB)。
- **工具链**:WiX `dotnet tool install --global wix --version "6.0.*"`。**WiX v7 需接受 OSMF 商业 EULA**(`error WIX7015`),故钉在 **v6**(纯 MIT)。
- **形式**:自包含(self-contained,内嵌 .NET 10 运行时,对方免装)+ **多文件** publish(非单文件,MSI 装到 Program Files 更正宗、免单文件 temp 解压)。`-r win-x64`、`DebugType=none` 去 pdb。
- **WiX 源** `setup/DynamicIsland.wxs`:perMachine 装 `Program Files\DynamicIsland`,`<Files Include="!(bindpath.pub)\**">` 批量 harvest 整个 publish 目录(免手列上百 dll),开始菜单快捷方式 + ARP 卸载项;固定 `UpgradeCode` 支持覆盖升级。**坑:`-b pub=...` bindpath 必须传绝对路径**,相对路径被解析成相对 wxs 目录(`setup/dist/publish`)→ harvest 0 文件、MSI 只剩 36KB(脚本里已用绝对路径,手动跑别用相对)。
- **验证**:perMachine 安装需管理员;静默 `/qn` 无法弹 UAC 提权会 1603(`MSI_LUA: ...no credential elevation`,非 MSI 缺陷),用 `Start-Process -Verb RunAs` 提权或交互安装。免提权快验内容用 `msiexec /a`(administrative install 解包,不建快捷方式)。
- **范围**:第一版只装 UI 应用本身;**不含**开机自启 / 应用图标 / 桌面快捷方式(用户勾选)。hook 采集接入仍由用户在自己项目 `.claude/settings.json` 配。

## 关键陷阱
- .NET 10 的解决方案文件是 `.slnx`(XML),不是 `.sln`。本仓库用 `DynamicIsland.slnx`。
- WPF 项目的 implicit usings **不含** `System.IO`(`StreamReader`/`File` 需显式 `using System.IO;`)。
- 动画抖动经验:① `ClipToBounds` + `DropShadowEffect` 同元素 → 方形阴影(拆成外阴影层 + 内裁剪层);② `UseLayoutRounding` 逐帧取整 → 动画抖动(已移除);③ 标题行 `VerticalAlignment=Center` 跟随高度动画会抖 → 用固定高度画布 + 顶部固定标题行,Pill 高度收缩只当裁剪窗。
- **详情卡两道裁剪边界**:外层 `Window Height` 与内层裁剪画布 `Grid Height` 都 `ResizeMode=NoResize` 不随子元素自动长大(当前 **340 / 300**;2026-06-19 详情卡改分组背景块后,两个 Border 的 padding + 卡间距额外撑高,从 280 / 232 上调)——多行清单撑高时两者都得够大,少一个就把卡片底部裁掉。高度按未完成 todo 行数动态算(含两块 padding + 卡间距)+ `Math.Min(画布高)` 双保险(`DetailTargetHeight`)。
- **截图验证灵动岛要 `SetProcessDPIAware()`**:PowerShell 默认非 DPI-aware,`CopyFromScreen`/`GetWindowRect` 坐标系与 WPF 物理像素不一致(本机 2560@150% → 逻辑 1707),会截错位/降采样发糊。按进程 PID `EnumWindows` 拿窗口 rect、`SetCursorPos` 到药丸中心触发 hover 再截。
- **transcript 尾窗必须能回退扩窗**(2026-06-19 真实端到端暴露并修复):真实会话末尾常有接近/超过 256KB 的超大单行(图片 base64、整文件读取);该行正在写入时固定 256KB 尾窗内可能一条完整 `usage` 都没有 → 间歇读空、详情卡模型/上下文不刷新。`ReadLatest` 没命中就 4× 扩窗重读兜底。**干净 curl 测试碰不到,只有真实密集会话才暴露。**
- **真实会话没有 TodoWrite**(2026-06-19 修):当前 Claude Code 使用 `TaskCreate` / `TaskUpdate` / `TaskList` 任务系统,`~/.claude/todos/` 为空,因此不能靠 `TodoWrite` hook 判断详情卡任务是否跑通。任务 id 不在 `TaskCreate.tool_input`,而在配对 `tool_result` 的 `Task #N created...` 文本里;修复走 transcript 全量重建。

- **状态机三个易错点(2026-06-19 修)**:① 毫秒级工具(Read/Edit/Write)真实会话里 **Pre/Post 同秒**到达,`PostToolUse` 若立即 `SetStatus(Thinking)` 会把「⚙ 工具名」瞬间盖掉、视觉上永远停在「思考中」→ 用 `_toolHoldTimer`(`ToolHoldSeconds=1.2`)延迟回落:`switch` 前统一 `Stop()`、`PostToolUse` 仅在 `_current==RunningTool` 时 `Start()`,连续工具无缝衔接、停顿超 1.2s 才回「思考中」。② 展开详情卡顶部 `TitleRow` 是「圆点 + Label」一组居中,**idle 也写「空闲」文字**会把圆点挤偏 → idle 隐藏 Label 让圆点单独居中(工作态有文字仍一组居中,符合「有文字共同居中、无文字圆点居中」)。**只有真实密集会话才暴露工具秒杀;干净 curl 测试碰不到。** ③ **`Notification` 有两种语义**:仅工具权限请求(`message` 含 `permission`,如「Claude needs your permission to use Bash」)是真「等待批准」;但**空闲 60s 等待输入**(`message`=「Claude is waiting for your input」)同样发 `Notification` 事件,若也无脑设 `WaitingApproval`,会把已完成/空闲态**永久误标**(该态无超时回落,不像 `Done` 有 4s timer)→ 任务结束后空闲片刻就卡死在「等待批准」。修法:`IngestEvent` 加 `Message` 字段、`IngestServer.Parse` 解析 `message`、`OnEvent` 仅 `IsApprovalRequest(message)` 才进 `WaitingApproval`,空闲提醒不打断当前态(`message` 缺失也按非权限处理,宁漏报勿误卡)。**这条干净 curl 即可复现/签收**(权限 vs 空闲两条 message 端到端对上)。**(2026-06-22 再修:乱序到达)** 上面只解决了「语义」,没解决「时序」。Claude Code 的 hooks 各自异步 `curl` 发出,投递到 App 的顺序可能乱序——属于「上一步操作」的工具权限 `Notification` 常比 `Stop` 晚几毫秒才到(真实日志 `Stop`(t) → `Notification`(t+1) 同会话),原代码无条件让它进 `WaitingApproval`,把刚变成 `Done` 的会话打回「等待批准」且永久卡死(WaitingApproval 是最高优先级 Weight 4,`PickActive` 永远选它,且 prune 无回落)。两处修:① `OnEvent` 的 `Notification` 分支加 `&& s.TurnRunning` 守卫——本轮已 `Stop`(TurnRunning=false)后到达的迟到 Notification 直接忽略;正常「先 Notification 后 PreToolUse」批准流程 TurnRunning 仍 true,不受影响。② `PruneSessions` 加 `WaitEvictSeconds=180` 兜底——即便仍有边角误标,3 分钟无后续事件强制回落 Idle,杜绝永久卡死。**真实会话乱序才暴露,干净 curl 顺序发碰不到**(要 Stop 紧跟迟到 Notification 才复现)。

- **SwiftUI 三件套的坑(2026-06-19)**:① **PUA 图标 glyph 用 `\uXXXX` 转义写** —— Segoe Fluent / Lucide 码位是私用区,模型输出常把 PUA 字符吞成空串(`""`),转义是纯 ASCII 传输无损(用 python 读 `ord` 核对码位)。② **`TextBlock.Inlines` 与 `.Text` 互斥** —— 改图标混排须把所有 `.Text=` 全切成 `Inlines`,留半个就清空。③ **嵌入字体 pack URI 用 `new FontFamily(new Uri("pack://application:,,,/"), "./Assets/#family")`** —— 字符串式 `"pack://...#family"` 会静默加载失败、图标显示 tofu □(family name 用 `[Windows.Media.Fonts]::GetFontFamilies(file)` 读)。④ **spring 缓动末端必须归一化 `Ease(1)=1`** —— 欠阻尼残留过冲会让 `DoubleAnimation` 永久停在超尺寸值;`SpringEase{EasingMode=EaseIn}` 透传(默认 EaseOut 会反射翻转曲线)。⑤ **真 squircle 在小圆角占比下肉眼 ≈ 普通圆角** —— 纯黑岛 r=14 看不出连续曲率,须配合展开态加大半径(14→24)才显威,印证早先「squircle ROI 低」判断。

- **内存:transcript 读取是大头,非 WPF/线程之罪**(2026-06-19 修,实测 Priv 230MB→107MB):① **诊断口径**——任务管理器「内存」是工作集(WS,含共享 DLL/换出页,虚高);真实占用看私有字节(Priv)。本机 **32 逻辑核 → ~57 线程是 .NET 线程池基线(最小线程数=核数)+ WPF/运行时,启动就有、非 bug**,基本动不了;WPF + .NET 地板 Priv ~100MB。② **真凶**——`IslandWindow.OnEvent` 旧版**每个 hook 事件**都 `LoadTranscriptAsync` 全量读整个 transcript(长会话实测 **8.15MB**):`ReadTaskSnapshot` 逐行解析重建 Task + `ReadLatest` 扩窗;且 `TrackToolUse` 对**所有** tool_use `input.Clone()` 驻留字典(几百个 Read/Bash/Edit 的大 input,可详情卡只用 TaskCreate)。几个事件就把 Priv 从 102MB 冲到 230MB,大对象进 LOH、GC 不及时还 OS。③ **治本四招**——transcript **只在 hover 详情卡可见时读**(非 hover 看不见,`OnEvent` 不读;`EnterDetail`/`_hoverTimer` 才读)+ **按文件长度去重**(`Session.LastTranscriptLen` 未变跳过全量解析)+ `TrackToolUse` **只 clone TaskCreate**(其余工具跳过,hover 单次峰值 227MB→129MB)+ 会话回 Idle 时 `TrimWorkingSet`(`GC.Collect`+LOH `CompactOnce`+`SetProcessWorkingSetSize(-1,-1)`,节流 20s,WS 可降到 32MB)+ csproj `ServerGarbageCollection=false`/`ConcurrentGarbageCollection=false`。④ **实测**——非 hover 灌 40 个带 8MB path 的密集事件只涨 4MB(稳态 107MB);hover 读一次 129MB;WS trim 后 32MB。**干净 curl 测不出(要真实大 transcript + 高频事件才暴露),本会话自身高频 hook 正好压出来。**

## 约束 / 注意
- **未经明确批准,不要碰全局 `~/.claude/settings.json`,不要破坏用户的 ccstatusline。** 所有 hook 接入走项目级 `.claude/settings.json`。
- `~/.codex/config.toml` 敏感,已备份为 `config.toml.bak`。
- hooks 在 **会话启动时加载**:在已开着的窗口中途改 settings / 调 effort,对那个窗口不生效,需 **新开 `claude` 会话** 才会按新配置上报。
- **Claude 端到端已实测打通(2026-06-19)**:本仓库这个 Claude Code 环境**会执行项目级 shell hooks** —— 当前会话自身的工具调用即真实驱动 App(`effort=max`/`session_id`/工具名逐一对上),`claude -p` headless 独立会话(全新 UUID)亦照常上报。普通交互式终端只会更标准。(早先「agent 环境可能不执行 hooks」的担忧已被推翻。)中途改 settings / 调 effort 仍需新开会话才生效(hooks 会话启动时加载)。

## 里程碑
- **M1 ✅** Claude Code 采集 + 基础药丸
- **M2 ✅** 状态机 + 灵动岛形变动画 + hover 详情卡(**todo 清单 + 进度** / 目录 / 模型 / 强度 / 上下文,按 session 分桶)。**Claude 端到端实测签收(2026-06-19)**:真实 hooks 驱动 + transcript 读数 + UI 渲染全绿;过程中修了 transcript 尾窗读空的竞态。详情卡 todo 由单行升级为**多行清单 + 进度(完成项折叠计数)**,并从真实 Claude `TaskCreate` / `TaskUpdate` transcript 历史重建任务,保留 `TodoWrite` 兼容路径。另修两个状态显示 bug:**工具态延迟回落**(毫秒级工具 Pre/Post 同秒不再被秒杀回「思考中」,Post 后保留 ~1.2s 再回落)+ **idle 展开圆点居中**(去掉「空闲」文字让状态圆点单独居中)。随后按用户审美把详情卡重整为**全部居中 + 分组背景块**(`TaskCard` 任务清单组 / `MetaCard` 元信息组两个浅色圆角块,内容全居中,无 todo 时任务块整体隐藏只剩元信息块),两道裁剪上限随之上调到 Window 340 / Grid 300(端到端三态截图签收:work 真实中文清单 + empty 仅元信息块 + 7 行最高情况不裁底)。**SwiftUI 三件套精修签收(2026-06-19)**:① 线性图标替彩色 emoji(Segoe Fluent Icons + 打包 Lucide 补 cpu/bar-chart),单色随 `Foreground` 得灰阶层级;② `SpringEase` 阻尼弹簧(末端归一化防超尺寸)替 `CubicEase`;③ 真 squircle 连续曲率几何(Lamé 超椭圆 `SquircleShape` 背景 + `ClipHost` squircle 裁剪,展开态 `PillRadius` 14→24 动画)。**本会话自身工具调用真实驱动验证**:放大截图见 `⚙ Bash` 工具态 + 真实 Task 清单(#28/#29) + `cpu opus-4-8 / max / bar-chart 328k`,8 图标全单色线性、基线对齐、squircle 圆角饱满。**(2026-06-19 续修)** 按用户反馈再修两点:① `Notification` 双语义致「结束后总误卡等待批准」——解析 `message` 区分工具权限请求 vs 空闲等待输入,仅前者进 `WaitingApproval`(两条 message 端到端截图签收:空闲→保持绿「完成」、权限→橙「等待批准」);② hover 展开/收起动画整体减速 30%(时长 ×1.3,缓解过快 + spring overshoot 回弹的「看得出中间帧」感);③ **内存** Priv 230MB→107MB(transcript 改 hover 才读 + 文件长度去重 + 只 clone TaskCreate + 空闲 trim + Workstation 非并发 GC,详见关键陷阱「内存」条)。**完成态绿色流光环绕(2026-06-19)**:任务结束(Done)时一道绿色辉光弧沿 squircle 边缘绕 2 圈(3 秒)后淡出,回静态绿「完成」。走 WPF 原生 `StrokeDashArray` + `StrokeDashOffset` 描边动画(渲染线程驱动,平时 `GlowPath` Collapsed/`Data=null` 零开销,故弃逐帧自绘的彗星拖尾方案);新增 `GlowPath` 描边层(`Pill` 内最上层、不裁剪,绿 `DropShadowEffect` 跟随轮廓发光不方形)+ `Squircle.Perimeter` 近似算 dash 单位(避免逐帧 flatten);`SetStatus` 进 Done 触发 `StartGlow`、离开立即 `StopGlow`(幂等),几何随 `BgShape.SizeChanged` 仅可见时跟随 Pill 尺寸过渡。亮弧占周长 30%、`SineEase` 两端略缓、offset 负向(顺时针观感),`GlowArcFraction/GlowLoops/GlowDurationSeconds` 等为可调常量。端到端截图签收:横条态绿弧绕行(4 帧位移)+ 3.6s 后消失回静态绿 + 打断(UserPromptSubmit)转思考态无残留。
- **M3 ✅** 多会话「主药丸 + 副会话点」+ 抽 `DynamicIsland.Core`(2026-06-19)。**两块都做**:① 采集层 5 文件机械搬迁到 `DynamicIsland.Core`(net10.0 Library,零 WPF 依赖,为单测铺路),namespace App→Core、App 加 ProjectReference + 改 3 行 using、slnx 加 Core,0 警告 0 错误、零行为变化签收。② 多药丸 UI:活跃会话从「最后事件」改为**优先级 `PickActive()`**(WaitingApproval > RunningTool > Thinking > Done > Idle,同级最近活动),`SetStatus` 拆成 `ApplyStatus`(改数据)+ `RenderPill`(画主药丸)+ `RenderActive`(调度);副会话点 `RenderSatellites`(状态色小圆点贴右侧、超 4 折叠 +N、hover 淡出);详情卡 `SessionTabs` 会话切换行(auto 主会话 vs pin 锁定,`_activeRendered`/`_displayed`/`_pinnedKey`);`_pruneTimer`(2s)管生命周期(Done 6s 回落 / Idle 30s 移除,替代 `_doneTimer`);ToolHold 绑当前主会话。**端到端实测签收**:本会话自身(`f048330e`,`effort=max`)实时驱动 App,灌 A/B/C 三测试会话验证优先级选主(运行态 `f048330e` 正确压过 idle A/B/C)、prune 回落、**内存守护**(测试会话无 transcript_path 零读取,仅 `_displayed` 在 hover 时读;Priv 230MB 含真实 8MB transcript hover 峰值,与文档一致)、生命周期定时器无崩溃。新增 `scripts/shot.ps1`/`shot2.ps1`(DPI-aware 按 PID 截灵动岛,留作回归)。**(2026-06-22 UI 重做)** 用户反馈旧版「主药丸 + 副会话点」是灾难:副点 7×7 单侧甩在药丸右侧拖尾、破坏灵动岛顶部居中对称,且像噪点不像 UI;详情卡 SessionTabs 也乱(无数量上限会撑破卡宽)。重做:① 副点收进药丸内部(`TitleRow` 内 `Label` 后,前置 1px 细竖分隔条 `SatSeparator`),尺寸调 9×9 与主圆点同量级,`_compactWidth += SatelliteStripWidth()` 让药丸**对称变宽**容纳副点 → 整组 `[● 文字 ┊ ● ● ●]` 仍居中、是一个连续 squircle 无外挂;`MaxSatellites` 4→3;hover 展开时副点整组收起(挤偏居中标题);`RenderActive` 脏检查加 `_renderedSatWidth`(副点增减/变色触发横条态重画);删 `PositionSatellites`/`FadeSatellites`。② SessionTabs 整理成一行统一标签(`MaxWidth=56`、统一圆角/间距、`MaxSessionTabs=3` 折叠 +N 且当前 pin 会话务必在列)。**端到端截图签收**:收起态药丸中心 x=419≈窗口中心 420 对称、副点在岛内状态色可分;hover 展开 SessionTabs 一行整齐不裁底。0 警告 0 错误。
- **发行 v1.0.0 ✅**(2026-06-21)第一个发行版打成 **Setup(MSI)**。自包含多文件 publish(内嵌 .NET 10,对方免装)→ WiX v6 打 `dist/DynamicIsland-1.0.0-win-x64.msi`(51MB,零警告)。新增 `setup/DynamicIsland.wxs` + `scripts/build-release.ps1`,App csproj 补 `Version/Company/Product`。装到 Program Files + 开始菜单快捷方式 + ARP 卸载项。**端到端签收**:`msiexec /a` 解包验证文件齐全 → `RunAs` 提权安装 ExitCode 0 → Program Files\DynamicIsland\DynamicIsland.App.exe 在位、开始菜单 `DynamicIsland.lnk`、ARP 显示「DynamicIsland 1.0.0」。过程踩坑:WiX v7 OSMF EULA(降 v6)、bindpath 必须绝对路径(否则 harvest 0 文件)、perMachine 静默装无法提权(1603,需 RunAs)。
- **M4 ✅** 系统信息 / 媒体（2026-06-22）：`IInfoSource` 通用接口 + `MediaSource`（WinRT 正在播放）+ `BatterySource`（Win32 电量）；agent 全空闲时由信息源接管主药丸横条态显示歌名/电量，agent 活跃则信息源静默。TFM 升 `net10.0-windows10.0.19041.0` 启 WinRT；`IslandStatus.Ambient` 新增；`RenderActive` 脏检查纳入 ambient 维度；PUA 图标用 `(char)0xXXXX` 运行时转换避免源码传输丢失。0 警告 0 错误，App 启动存活验证。
- **M5 ⬜** 工程化:点击穿透、托盘退出、多屏 + DPI、全屏自动隐藏、开机自启
- **Codex ⬜** hooks 在 Windows exec 下未送达,待查
- 待修:`DESIGN.md` §4.2 与 `setup/codex-config-snippet.toml` 的 Codex command 数组 → 字符串形式
