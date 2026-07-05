using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using DynamicIsland.Core.Ingest;
using DynamicIsland.Core.Model;

namespace DynamicIsland.App;

/// <summary>
/// 灵动岛悬浮窗（M2 + 详情卡片）：横条态显示状态；鼠标悬停展开成卡片，显示
/// 目录 / 模型 / 思考强度 / 已使用上下文。内容是固定高度画布，Pill 高度收缩只当裁剪窗，
/// 故标题行不随高度抖动；展开收回用宽高同步形变 + 详情淡入淡出。
/// </summary>
public partial class IslandWindow : Window
{
    private const double CompactWidth = 84;        // 紧凑（空闲）态宽度（横条）
    private const double PillCompactHeight = 28;
    private const double DotSize = 9;
    private const double LabelLeftGap = 8;
    private const double SidePadding = 14;
    private const double DetailWidth = 260;
    private const double DetailHeightBase = 96;    // 无 todo 时回落：仅元信息块（32 顶 + 16 卡 padding + 38 两行 + 10 底）

    // —— 多行 todo 清单 + 进度（详情卡）——
    private const int MaxTodoRows = 6;             // 未完成项最多铺开行数，超出折叠成“… 还有 X 条”
    private const double TodoFontSize = 12;
    private const double TodoRowGap = 3;           // 每条 todo 行间距
    // 高度估算（首版常量；截图后可微调 1–2px）
    private const double DetailChromeTop = 32;     // = DetailPanel.Margin.Top（标题区下沿 → 详情起点）
    private const double ProgressRowHeight = 18;
    private const double TodoRowHeight = 19;       // 单条 todo 行（12px 字 + 行盒 + 间距）
    private const double CwdMetaBlockHeight = 38;  // 目录 + meta 两行
    private const double DetailBottomPad = 10;
    private const double CardPadV = 8;             // 背景块竖直内边距（单边，= XAML Border Padding 纵向值）
    private const double CardGap = 8;              // 任务块与元信息块间距（= MetaCard Margin.Top）
    private const double CardContentWidth = 208;   // 背景块内容宽（232 − 2×12 Padding），= todo 行 MaxWidth
    private const double DetailCanvasHeight = 300; // = XAML 内层裁剪画布 Grid.Height，高度上限双保险
    private const double SessionTabsHeight = 26;   // 多会话切换行高（仅 _sessions.Count>1 时计入详情卡高度）

    // 工具名延迟回落：PostToolUse 后保留「⚙ 工具名」这么久，毫秒级工具（同秒 Pre+Post）也看得见
    private const double ToolHoldSeconds = 1.2;

    // —— 多会话生命周期（M3）——
    private const double DoneLingerSeconds = 6;   // 完成态保留多久后数据回落 Idle（替代旧单例 _doneTimer 的 4s）
    private const double IdleEvictSeconds = 30;   // Idle 多久后从 _sessions 移除，防字典无限增长
    private const double WaitEvictSeconds = 180;  // 等待批准最长保留（兜底）：超时无任何后续事件则回落 Idle，防误标永久卡死
    private const int MaxSatellites = 3;          // 副会话点最多铺开数，超出折叠成「+N」（收进岛内，宜少）
    private const int MaxSessionTabs = 3;          // 详情卡会话切换行最多标签数，超出折叠成「+N」（详情卡宽 ~260）

    // —— 完成态流光：绿色辉光弧沿 squircle 边缘绕圈（StrokeDashArray 原生描边动画，渲染线程驱动，平时零开销）——
    private const double GlowArcFraction = 0.30;       // 亮弧占周长比例（其余透明）
    private const int GlowLoops = 2;                   // 3 秒内绕圈数
    private const double GlowDurationSeconds = 3.0;
    private const double GlowFadeInSeconds = 0.25;
    private const double GlowFadeOutSeconds = 0.4;

    private static readonly Color BaseBg  = Color.FromArgb(0xF0, 0x12, 0x12, 0x14);
    private static readonly Color HoverBg = Color.FromArgb(0xFF, 0x22, 0x22, 0x28);

    private static readonly Brush IdleBrush     = MakeBrush(0x88, 0x8A, 0x90); // 灰
    private static readonly Brush ThinkingBrush = MakeBrush(0x4C, 0x8D, 0xFF); // 蓝
    private static readonly Brush ToolBrush     = MakeBrush(0x2E, 0xC5, 0xCE); // 青
    private static readonly Brush WaitBrush     = MakeBrush(0xFF, 0xA5, 0x2E); // 橙
    private static readonly Brush DoneBrush     = MakeBrush(0x3D, 0xD5, 0x6B); // 绿
    private static readonly Brush AmbientBrush  = MakeBrush(0xB0, 0xB3, 0xBA); // 信息源（媒体/电池）接管时的柔白圆点

    private static readonly Brush TodoDoneBrush    = MakeBrush(0x6E, 0x72, 0x7A); // todo 已完成：暗灰
    private static readonly Brush TodoPendingBrush = MakeBrush(0x9A, 0x9C, 0xA4); // todo 待办：中灰

    // —— 线性图标（Segoe Fluent Icons，Win11 自带零打包；单色随 Foreground，替彩色 emoji）——
    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    private static class Ic
    {
        public const string List   = ""; // 进度/清单 BulletedList
        public const string Folder = ""; // 目录 Folder
        public const string Model  = ""; // 模型 Processing（Fluent 无芯片字，近似）
        public const string Bolt   = ""; // 思考强度 LightningBolt
        public const string Chart  = ""; // 上下文 Diagnostic（Fluent 无柱状字，近似）
        public const string Gear   = ""; // 工具运行 Settings
        public const string Warn   = ""; // 等待批准 Warning
        public const string Done   = ""; // 完成 Completed
        public const string Play   = ""; // todo 在做 Play
        public const string Circle = ""; // todo 待办 CircleRing
    }

    // Lucide（打包 ttf）：补 Fluent 没有贴切字的两个——模型芯片 / 上下文柱状图
    private static readonly FontFamily LucideFont = new(new Uri("pack://application:,,,/"), "./Assets/#lucide");
    private static class Lc
    {
        public const string Cpu   = ""; // 模型：cpu 芯片
        public const string Chart = ""; // 上下文：bar-chart-3 柱状图
    }

    private readonly DispatcherTimer _hoverTimer;
    private readonly DispatcherTimer _toolHoldTimer; // 工具结束后延迟回落「思考中」，见 ToolHoldSeconds
    private readonly DispatcherTimer _pruneTimer;    // 多会话生命周期：Done 回落 + Idle 清理（替代单例 _doneTimer）
    private readonly TextBlock _measure = new();
    private readonly SolidColorBrush _bgBrush = new(BaseBg);

    private IslandStatus _current = IslandStatus.Idle;
    private string _statusText = "";
    private double _compactWidth = CompactWidth;

    // 每会话一份快照（模型/上下文从 transcript 读，强度从 hook）。活跃会话驱动药丸与详情卡，
    // 多个项目并发也互不串台；M3 多药丸时每个 Session 各点亮一颗。
    private sealed class Session
    {
        public string Key = "";              // session_id（反查：副点点击 / pin / prune）
        public DateTime LastActivity;        // 每次 OnEvent 命中刷新；prune 与同权重排序据此
        public string Cwd = "";
        public string Tool = "";
        public string Model = "—";
        public string Effort = "—";
        public string Context = "—";
        public string CurrentTask = "";
        public IReadOnlyList<TodoItem> Todos = Array.Empty<TodoItem>(); // 多行清单数据源（空数组：渲染端不判 null）
        public string? TranscriptPath;
        public long LastTranscriptLen = -1; // 上次读取时的文件长度；未变则跳过全量重读（省大会话每事件几 MB 的解析 + LOH 分配）
        public IslandStatus Status = IslandStatus.Idle;
        public string StatusText = "";
        public DateTime TurnStart;
        public TimeSpan LastTurnElapsed = TimeSpan.Zero;
        public bool TurnRunning;
    }

    private readonly Dictionary<string, Session> _sessions = new();
    // —— 多会话渲染状态（M3）——
    private Session? _activeRendered;                       // 主药丸当前渲染的会话（PickActive 选出，脏检查用）
    private IslandStatus _renderedStatus = IslandStatus.Idle; // 主药丸渲染态（脏检查：会话/状态没变不重画）
    private double _renderedSatWidth = 0;                   // 上次渲染的副点区宽度（脏检查：副会话增减/变色也触发横条态重画）
    private Session? _displayed;                            // 详情卡当前显示的会话（hover 中 = pin 或主会话）
    private Session? _toolHoldTarget;                       // _toolHoldTimer 武装时的目标会话（只对它回落）
    private string? _pinnedKey;                             // 用户在详情卡锁定要看的 session_id；null = 跟随主会话
    private bool _hovering;
    private double _glowUnit; // 周长 / 线宽 = StrokeDashArray 与 StrokeDashOffset 的单位；StartGlow 时缓存供 offset 动画

    // —— 非 agent 信息源（M4）：agent 全空闲时由第一个有内容的源接管主药丸 ——
    private readonly List<SystemInfo.IInfoSource> _sources = new();
    private string _ambientGlyph = "";   // 当前接管源的图标（RenderPill 横条态用）
    private string _ambientText = "";    // 当前接管源的文字

    public IslandWindow()
    {
        InitializeComponent();

        _hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        // hover 期间每秒拉一次（agent 在跑时上下文会涨）；LoadTranscriptAsync 内按文件长度去重，
        // 文件没变直接跳过，不会真的每秒全量读 8MB；读到新数据自行 RefreshDetail。只拉当前展示的会话。
        _hoverTimer.Tick += (_, _) => { if (_hovering && _displayed != null) LoadTranscriptAsync(_displayed); };

        // 工具名延迟回落：Tick 到点若目标会话仍是主会话且停在某工具（没被新事件改写），才回「思考中」
        _toolHoldTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(ToolHoldSeconds) };
        _toolHoldTimer.Tick += (_, _) =>
        {
            _toolHoldTimer.Stop();
            var t = _toolHoldTarget;
            if (t != null && t.Status == IslandStatus.RunningTool)
            {
                ApplyStatus(t, IslandStatus.Thinking, "思考中…");
                RenderActive();
            }
        };

        // 多会话生命周期：每 2s 扫一遍——Done 超 DoneLingerSeconds 回落 Idle、Idle 超 IdleEvictSeconds 移除。
        _pruneTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _pruneTimer.Tick += (_, _) => PruneSessions();
        _pruneTimer.Start();

        Loaded += (_, _) =>
        {
            _measure.FontFamily = Label.FontFamily;
            _measure.FontSize = Label.FontSize;
            BgShape.Fill = _bgBrush;
            ClipHost.SizeChanged += (_, _) => UpdateClip(PillRadius);
            // 流光仅在可见（完成态）时随 Pill 尺寸过渡跟随轮廓重画；平时不画，零开销
            BgShape.SizeChanged += (_, _) => { if (GlowPath.Visibility == Visibility.Visible) UpdateGlowGeometry(); };
            Reposition();
        };

        Pill.MouseEnter += (_, _) => { AnimateBg(HoverBg); EnterDetail(); };
        Pill.MouseLeave += (_, _) => { AnimateBg(BaseBg); LeaveDetail(); };
    }

    /// <summary>处理一条采集事件（必须在 UI 线程调用）。按 session 归桶写数据，再按优先级重选主会话渲染。</summary>
    public void OnEvent(IngestEvent evt)
    {
        var key = string.IsNullOrEmpty(evt.SessionId) ? "(default)" : evt.SessionId!;
        if (!_sessions.TryGetValue(key, out var s))
        {
            s = new Session();
            _sessions[key] = s;
        }
        s.Key = key;
        s.LastActivity = DateTime.Now;

        if (!string.IsNullOrEmpty(evt.Cwd)) s.Cwd = evt.Cwd!;
        if (!string.IsNullOrEmpty(evt.Effort)) s.Effort = evt.Effort!;
        if (!string.IsNullOrEmpty(evt.TranscriptPath)) s.TranscriptPath = evt.TranscriptPath;
        if (!string.IsNullOrEmpty(evt.CurrentTask)) s.CurrentTask = evt.CurrentTask!;
        if (evt.Todos is { Count: > 0 }) s.Todos = evt.Todos; // 仅非空覆盖：旧清单在后续非 TodoWrite 事件中保留

        _toolHoldTimer.Stop(); // 任何新事件都取消待回落；PostToolUse 会重新武装

        switch (evt.EventName)
        {
            case "UserPromptSubmit":
                s.TurnStart = DateTime.Now; s.TurnRunning = true; s.Tool = "";
                ApplyStatus(s, IslandStatus.Thinking, "思考中…");
                break;
            case "PreToolUse":
                // TodoWrite 不是“动作”而是进度更新：不抢标题，维持思考态，只更新当前任务。
                if (string.Equals(evt.Tool, "TodoWrite", StringComparison.OrdinalIgnoreCase))
                {
                    ApplyStatus(s, IslandStatus.Thinking, "思考中…");
                }
                else
                {
                    s.Tool = evt.Tool ?? "工具";
                    ApplyStatus(s, IslandStatus.RunningTool, s.Tool);
                }
                break;
            case "PostToolUse":
                // 工具结束不立即回落：保留「⚙ 工具名」~ToolHoldSeconds，毫秒级工具（同秒 Pre+Post）也看得见；
                // 期间来新事件会在 switch 前 Stop 并重设。只对当前主会话回落（副点只显状态色，无需防抖）。
                if (s.Status == IslandStatus.RunningTool) { _toolHoldTarget = s; _toolHoldTimer.Start(); }
                break;
            case "Notification":
                // Notification 有两种语义：① 工具权限请求 ② 空闲 60s 等待输入。仅前者是「等待批准」。
                // 额外守卫：仅 TurnRunning（本轮未结束）才允许进 WaitingApproval，防止 Stop 先到后迟到的
                // Notification（curl 异步乱序）把已 Done 的会话打回 WaitingApproval 且无法自动回落。
                if (IsApprovalRequest(evt.Message) && s.TurnRunning)
                    ApplyStatus(s, IslandStatus.WaitingApproval, "等待批准");
                break;
            case "Stop":
                if (s.TurnRunning) { s.LastTurnElapsed = DateTime.Now - s.TurnStart; s.TurnRunning = false; }
                ApplyStatus(s, IslandStatus.Done, "完成"); // Done 数据回落交给 _pruneTimer（DoneLingerSeconds）
                break;
        }

        RenderActive();

        // transcript 全量解析（大会话单次几 MB）只在详情卡可见、且这条事件属于当前展示会话时做：
        // 非 hover 时模型/上下文/任务都看不见，没必要每个 hook 事件全量重读（私有内存涨到 200MB+ 的主因）。
        if (_hovering && ReferenceEquals(s, _displayed))
        {
            LoadTranscriptAsync(s);
            RefreshDetail();
            AnimatePill(DetailWidth, DetailTargetHeight(), 0.29);
        }
    }

    // —— 数据层：只写会话状态，不碰任何 XAML（OnEvent / prune / toolHold 调用）——
    private void ApplyStatus(Session s, IslandStatus status, string text)
    {
        s.Status = status;
        s.StatusText = text;
    }

    // —— 优先级选主：WaitingApproval > RunningTool > Thinking > Done > Idle，同权重取最近活动 ——
    private static int Weight(IslandStatus s) => s switch
    {
        IslandStatus.WaitingApproval => 4,
        IslandStatus.RunningTool     => 3,
        IslandStatus.Thinking        => 2,
        IslandStatus.Done            => 1,
        _                            => 0
    };

    private Session? PickActive()
    {
        Session? best = null;
        foreach (var s in _sessions.Values)
        {
            if (best == null
                || Weight(s.Status) > Weight(best.Status)
                || (Weight(s.Status) == Weight(best.Status) && s.LastActivity > best.LastActivity))
                best = s;
        }
        return best;
    }

    // —— 非 agent 信息源（M4）：注册 + 选取当前接管源 ——
    /// <summary>注册一个信息源并启动；其 Changed 事件由 App 切回 UI 线程后调 OnSourceChanged。</summary>
    public void AddSource(SystemInfo.IInfoSource src)
    {
        _sources.Add(src);
        src.Start();
    }

    /// <summary>信息源数据变化（已在 UI 线程）：仅 agent 全空闲时才可能影响显示，重画即可。</summary>
    public void OnSourceChanged() => RenderActive();

    // 第一个有内容（Label 非空）的信息源；媒体优先于电池（注册顺序决定）。无则 null。
    private SystemInfo.IInfoSource? ActiveInfoSource()
    {
        foreach (var src in _sources)
            if (!string.IsNullOrEmpty(src.Label)) return src;
        return null;
    }

    // —— 调度层：选主会话、脏检查后重画主药丸、刷副点、hover 时刷详情卡 ——
    private void RenderActive()
    {
        var pick = PickActive();
        double satW = SatelliteStripWidth();
        // 空闲时信息源接管：把接管源的内容纳入脏检查（换歌/电量变化也要重画）。
        var src = (pick == null || pick.Status == IslandStatus.Idle) ? ActiveInfoSource() : null;
        string ambGlyph = src?.Glyph ?? "";
        string ambText = src?.Label ?? "";
        if (!ReferenceEquals(pick, _activeRendered)
            || (pick?.Status ?? IslandStatus.Idle) != _renderedStatus
            || satW != _renderedSatWidth
            || ambGlyph != _ambientGlyph
            || ambText != _ambientText)
        {
            _activeRendered = pick;
            _renderedStatus = pick?.Status ?? IslandStatus.Idle;
            _renderedSatWidth = satW;
            _ambientGlyph = ambGlyph;
            _ambientText = ambText;
            RenderPill(pick); // _compactWidth 重算（含副点区）→ 横条态宽度对称跟随
        }
        RenderSatellites();

        if (_hovering)
        {
            // 详情卡跟随：未 pin 时显示主会话；pin 的会话已被 prune 则回落自动
            ResolveDisplayed();
            RefreshDetail();
            AnimatePill(DetailWidth, DetailTargetHeight(), 0.29);
        }
    }

    // 多会话生命周期：Done 回落 Idle、Idle 清理；有变化则重选主会话并重画。
    private void PruneSessions()
    {
        var now = DateTime.Now;
        bool changed = false;
        List<string>? remove = null;
        foreach (var (key, s) in _sessions)
        {
            if (s.Status == IslandStatus.Done && (now - s.LastActivity).TotalSeconds > DoneLingerSeconds)
            {
                s.Status = IslandStatus.Idle; s.StatusText = ""; changed = true;
            }
            // 等待批准兜底：批准动作在终端完成、本程序收不到「已批准」事件，正常会有后续 Pre/PostToolUse
            // 推进状态；但若误标或会话静默，WaitingApproval（最高优先级）会永久占据主药丸 → 超时强制回落 Idle。
            if (s.Status == IslandStatus.WaitingApproval && (now - s.LastActivity).TotalSeconds > WaitEvictSeconds)
            {
                s.Status = IslandStatus.Idle; s.StatusText = ""; s.TurnRunning = false; changed = true;
            }
            if (s.Status == IslandStatus.Idle && (now - s.LastActivity).TotalSeconds > IdleEvictSeconds)
            {
                (remove ??= new()).Add(key); changed = true;
            }
        }
        if (remove != null)
            foreach (var k in remove) _sessions.Remove(k);

        if (changed)
        {
            RenderActive();
            // 全空 / 全 Idle 时把堆与工作集还给 OS
            bool allIdle = true;
            foreach (var s in _sessions.Values) if (s.Status != IslandStatus.Idle) { allIdle = false; break; }
            if (allIdle) TrimWorkingSet();
        }
    }

    // 详情卡显示哪个会话：pin 有效则 pin，否则跟随主会话；pin 悬空（会话已 prune）回落自动。
    private void ResolveDisplayed()
    {
        if (_pinnedKey != null && _sessions.TryGetValue(_pinnedKey, out var pinned))
            _displayed = pinned;
        else
        {
            _pinnedKey = null;
            _displayed = _activeRendered;
        }
    }

    // —— 渲染层：把“应显示在主药丸的会话”全量重画（颜色/文字/宽度/脉冲/流光）。s==null → Idle 外观 ——
    private void RenderPill(Session? s)
    {
        var status = s?.Status ?? IslandStatus.Idle;
        var text = s?.StatusText ?? "";
        _current = status;
        _statusText = text;

        // 空闲时信息源（M4）：有 ambient 内容则接管药丸显示，覆盖 idle 暗色外观
        bool ambient = status == IslandStatus.Idle && !string.IsNullOrEmpty(_ambientText);
        Dot.Fill = ambient ? AmbientBrush : ColorFor(status);
        // 横条态宽度 = 主状态宽 + 副点区宽（分隔条 + 副点）。药丸 HorizontalAlignment=Center + 对称变宽 → 整组居中，无右挂。
        _compactWidth = (status == IslandStatus.Idle && !ambient
                            ? CompactWidth
                            : MeasureWidth(ambient ? _ambientText : text, hasIcon: ambient && !string.IsNullOrEmpty(_ambientGlyph)))
                        + SatelliteStripWidth();

        if (!_hovering)
        {
            if (status == IslandStatus.Idle && !ambient)
            {
                AnimateWidth(CompactWidth, expanding: false);
                AnimateLabelOpacity(0.0, clearWhenDone: true);
            }
            else if (ambient)
            {
                Label.Visibility = Visibility.Visible;
                SetAmbientLabel(_ambientGlyph, _ambientText);
                AnimateWidth(_compactWidth, expanding: true);
                AnimateLabelOpacity(1.0);
            }
            else
            {
                Label.Visibility = Visibility.Visible;
                SetLabel(status, text);
                AnimateWidth(_compactWidth, expanding: true);
                AnimateLabelOpacity(1.0);
            }
        }
        else
        {
            // hover 展开：idle/ambient 均隐藏文字让圆点单独居中；非 idle 显示状态文字
            if (status == IslandStatus.Idle)
            {
                Label.Inlines.Clear();
                Label.Visibility = Visibility.Collapsed;
            }
            else
            {
                Label.Visibility = Visibility.Visible;
                Label.Opacity = 1.0;
                SetLabel(status, text);
            }
        }

        if (status == IslandStatus.Thinking) StartDotPulse(gentle: true);
        else if (status == IslandStatus.WaitingApproval) StartDotPulse(gentle: false);
        else StopDotPulse();

        if (status == IslandStatus.WaitingApproval && !_hovering) StartPillPulse();
        else StopPillPulse();

        // 完成态：绿色辉光弧绕药丸边缘 2 圈（3 秒）后淡出；离开 Done 立即收光
        if (status == IslandStatus.Done) StartGlow();
        else StopGlow();
    }

    // —— 副会话点（M3 重做）：除主会话外的活跃会话收进药丸内部（标题行右段，squircle 裁剪区内），
    //    药丸对称变宽容纳它们 → 整组仍居中，无右挂拖尾。按状态色，超 MaxSatellites 折叠「+N」 ——
    private const double SatelliteSize = 9;        // 与主圆点 Dot 同量级，不再是噪点
    private const double SatelliteGap = 7;         // 副点之间间距
    private const double SatSeparatorReserve = 18; // 分隔条占位（分隔条 Margin.Left 9 + Satellites Margin.Left 8 + 线宽 1）
    private static readonly Brush SatelliteOverflowBrush = MakeBrush(0x5A, 0x5C, 0x64); // 折叠点：暗灰

    // 当前应铺开的副会话（除主会话外的非 Idle），按优先级 + 最近活动排序。宽度估算与渲染共用，保证一致。
    private List<Session> ActiveSatellites()
    {
        var others = new List<Session>();
        foreach (var s in _sessions.Values)
            if (!ReferenceEquals(s, _activeRendered) && s.Status != IslandStatus.Idle)
                others.Add(s);
        others.Sort((a, b) =>
        {
            int w = Weight(b.Status).CompareTo(Weight(a.Status));
            return w != 0 ? w : b.LastActivity.CompareTo(a.LastActivity);
        });
        return others;
    }

    // 副点区在横条态占的额外宽度（分隔条 + n 颗点 + 颗间间距 + 可能的 +N 折叠点）。
    // 计入 _compactWidth → 药丸对称变宽、整组居中。idle 态 / 无副点时为 0。
    private double SatelliteStripWidth()
    {
        int total = ActiveSatellites().Count;
        if (total == 0) return 0;
        int dots = Math.Min(total, MaxSatellites) + (total > MaxSatellites ? 1 : 0);
        return SatSeparatorReserve + dots * SatelliteSize + (dots - 1) * SatelliteGap;
    }

    private void RenderSatellites()
    {
        Satellites.Children.Clear();

        var others = ActiveSatellites();
        if (others.Count == 0 || _hovering)
        {
            // 无副会话，或 hover 展开（详情卡 SessionTabs 接管）→ 整组隐藏
            SatSeparator.Visibility = Visibility.Collapsed;
            Satellites.Visibility = Visibility.Collapsed;
            return;
        }
        SatSeparator.Visibility = Visibility.Visible;
        Satellites.Visibility = Visibility.Visible;

        int shown = Math.Min(others.Count, MaxSatellites);
        for (int i = 0; i < shown; i++) Satellites.Children.Add(MakeSatelliteDot(others[i]));
        int rest = others.Count - shown;
        if (rest > 0) Satellites.Children.Add(MakeOverflowDot(rest));
    }

    private Ellipse MakeSatelliteDot(Session s)
    {
        var dot = new Ellipse
        {
            Width = SatelliteSize,
            Height = SatelliteSize,
            Fill = ColorFor(s.Status),
            Margin = new Thickness(0, 0, SatelliteGap, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            Tag = s.Key,
            ToolTip = ShortCwd(s.Cwd)
        };
        // 点副点：hover 中联动切详情卡显示该会话（pin）
        dot.MouseLeftButtonUp += (sender, _) =>
        {
            if (_hovering && sender is Ellipse e && e.Tag is string key) PinSession(key);
        };
        return dot;
    }

    private Border MakeOverflowDot(int rest) => new()
    {
        Width = SatelliteSize + 5,
        Height = SatelliteSize,
        CornerRadius = new CornerRadius(SatelliteSize / 2),
        Background = SatelliteOverflowBrush,
        VerticalAlignment = VerticalAlignment.Center,
        ToolTip = $"还有 {rest} 个会话",
        Child = new TextBlock
        {
            Text = $"+{rest}",
            Foreground = Brushes.White,
            FontSize = 7,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        }
    };

    // —— 详情卡会话切换行（Phase E）：每会话一个圆角标签，点击 pin；当前显示的高亮 ——
    private static readonly Brush TabBg       = MakeBrush2(0x1A, 0xFF, 0xFF, 0xFF); // 普通标签底
    private static readonly Brush TabBgActive = MakeBrush2(0x40, 0xFF, 0xFF, 0xFF); // 当前显示标签高亮

    private void RebuildSessionTabs()
    {
        SessionTabs.Children.Clear();
        if (!MultiSession)
        {
            SessionTabs.Visibility = Visibility.Collapsed;
            return;
        }
        SessionTabs.Visibility = Visibility.Visible;

        // 按优先级 + 最近活动排序，主会话在前
        var all = new List<Session>(_sessions.Values);
        all.Sort((a, b) =>
        {
            int w = Weight(b.Status).CompareTo(Weight(a.Status));
            return w != 0 ? w : b.LastActivity.CompareTo(a.LastActivity);
        });

        // 当前显示的会话务必在列（即便排在折叠区外），保证用户 pin 的标签可见且高亮
        int shown = Math.Min(all.Count, MaxSessionTabs);
        var visible = new List<Session>(all.GetRange(0, shown));
        if (_displayed != null && !visible.Contains(_displayed) && all.Contains(_displayed))
        {
            visible[shown - 1] = _displayed; // 顶掉折叠边界处一个，确保当前会话在列
        }
        foreach (var s in visible)
            SessionTabs.Children.Add(MakeSessionTab(s, ReferenceEquals(s, _displayed)));

        int rest = all.Count - shown;
        if (rest > 0) SessionTabs.Children.Add(MakeSessionTabsOverflow(rest));
    }

    private Border MakeSessionTab(Session s, bool active)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new Ellipse
        {
            Width = 7, Height = 7,
            Fill = ColorFor(s.Status),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 5, 0)
        });
        row.Children.Add(new TextBlock
        {
            Text = LeafCwd(s.Cwd), // 标签只显示末段目录名（".../a/b" 在窄标签里既被裁又冗余）
            Foreground = active ? Brushes.White : TodoPendingBrush,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 40 // 3 标签 + 折叠点 4 项 × (30 固定 + 40 文字) ≤ 252px ≤ 260 DetailWidth
        });
        var tab = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = active ? TabBgActive : TabBg,
            Padding = new Thickness(7, 3, 7, 3),
            Margin = new Thickness(2, 0, 2, 0),
            Cursor = Cursors.Hand,
            Tag = s.Key,
            ToolTip = ShortCwd(s.Cwd), // hover 显示完整短路径（标签里只显示末段名）
            Child = row
        };
        tab.MouseLeftButtonUp += (sender, _) =>
        {
            if (sender is Border b && b.Tag is string key) PinSession(key);
        };
        return tab;
    }

    // 会话标签折叠点：超 MaxSessionTabs 的会话数收成「+N」（与副点折叠同款语义，不可点）
    private Border MakeSessionTabsOverflow(int rest) => new()
    {
        CornerRadius = new CornerRadius(8),
        Background = TabBg,
        Padding = new Thickness(7, 3, 7, 3),
        Margin = new Thickness(2, 0, 2, 0),
        ToolTip = $"还有 {rest} 个会话",
        Child = new TextBlock
        {
            Text = $"+{rest}",
            Foreground = TodoPendingBrush,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center
        }
    };

    // 详情卡里锁定查看某会话（主药丸仍按优先级自动切换，互不干扰）。
    private void PinSession(string key)
    {
        if (!_sessions.ContainsKey(key)) return;
        _pinnedKey = key;
        ResolveDisplayed();
        if (_displayed != null) LoadTranscriptAsync(_displayed); // 切到新会话补一次首屏数据
        RefreshDetail();
        AnimatePill(DetailWidth, DetailTargetHeight(), 0.29);
    }


    // 空闲提醒 "Claude is waiting for your input" 不含 → false，不误报「等待批准」。message 缺失也按非权限处理。
    private static bool IsApprovalRequest(string? message)
    {
        if (string.IsNullOrEmpty(message)) return false;
        return message.Contains("permission", StringComparison.OrdinalIgnoreCase)
            || message.Contains("approve", StringComparison.OrdinalIgnoreCase)
            || message.Contains("approval", StringComparison.OrdinalIgnoreCase);
    }

    private static Brush ColorFor(IslandStatus s) => s switch
    {
        IslandStatus.Thinking        => ThinkingBrush,
        IslandStatus.RunningTool     => ToolBrush,
        IslandStatus.WaitingApproval => WaitBrush,
        IslandStatus.Done            => DoneBrush,
        IslandStatus.Ambient         => AmbientBrush,
        _                            => IdleBrush
    };

    // 状态行图标：Thinking 无图标（靠圆点呼吸），其余按状态取 Fluent glyph
    private static string? IconForStatus(IslandStatus s) => s switch
    {
        IslandStatus.RunningTool     => Ic.Gear,
        IslandStatus.WaitingApproval => Ic.Warn,
        IslandStatus.Done            => Ic.Done,
        _                            => null
    };

    // 往 TextBlock 追加「图标（IconFont）+ 文字（继承字体）」一段；图标基线居中对齐文字
    private static void AddIconRun(TextBlock tb, string glyph, string text, FontFamily? font = null)
    {
        tb.Inlines.Add(new Run(glyph) { FontFamily = font ?? IconFont, BaselineAlignment = BaselineAlignment.Center });
        if (!string.IsNullOrEmpty(text)) tb.Inlines.Add(new Run(" " + text));
    }

    // 状态行 Label：按状态补图标（走 Inlines，不用 .Text；雅黑无 PUA 字形，glyph 必带 IconFont）
    private void SetLabel(IslandStatus status, string text)
    {
        Label.Inlines.Clear();
        var g = IconForStatus(status);
        if (g != null) AddIconRun(Label, g, text);
        else Label.Inlines.Add(new Run(text));
    }

    // 信息源（M4）接管时的标签：图标（媒体音符 / 电池）+ 文字（歌名 / 电量）
    private void SetAmbientLabel(string glyph, string text)
    {
        Label.Inlines.Clear();
        if (!string.IsNullOrEmpty(glyph)) AddIconRun(Label, glyph, text);
        else Label.Inlines.Add(new Run(text));
    }

    // —— Hover 展开 / 收回：标题行布局固定，只动 Pill 尺寸与详情透明度 → 无抖动 ——
    private void EnterDetail()
    {
        _hovering = true;
        StopPillPulse();
        RenderSatellites(); // 收进药丸的副点在展开态会挤偏居中标题 → 整组收起（_hovering 判定），详情卡 SessionTabs 接管

        // 进入详情卡：未 pin → 显示当前主会话
        _pinnedKey = null;
        _displayed = _activeRendered;

        // idle 无状态文字 → 圆点单独居中；工作态才显示「⚙ 工具名 / 思考中」（圆点+文字一组居中）
        if (_current == IslandStatus.Idle)
        {
            Label.Inlines.Clear();
            Label.Visibility = Visibility.Collapsed;
        }
        else
        {
            Label.Visibility = Visibility.Visible;
            Label.Opacity = 1.0;
            SetLabel(_current, _statusText);
        }

        RefreshDetail();
        if (_displayed != null) LoadTranscriptAsync(_displayed); // 进入详情卡读一次最新（非 hover 期间不读，此处补首屏数据）
        BeginDouble(DetailPanel, OpacityProperty, 1.0, 0.34);
        AnimatePill(DetailWidth, DetailTargetHeight(), 0.39);
        AnimateRadius(24, 0.39);
        _hoverTimer.Start();
    }

    private void LeaveDetail()
    {
        _hovering = false;
        _hoverTimer.Stop();
        _pinnedKey = null; // 离开取消 pin，下次 hover 回自动跟随主会话
        Satellites.Opacity = 1.0;
        RenderSatellites(); // 回横条态：副点重新铺回药丸内部

        BeginDouble(DetailPanel, OpacityProperty, 0.0, 0.23);

        double targetW = _current == IslandStatus.Idle ? CompactWidth : _compactWidth;
        AnimatePill(targetW, PillCompactHeight, 0.36, expanding: false);
        AnimateRadius(14, 0.36);

        if (_current == IslandStatus.Idle)
        {
            AnimateLabelOpacity(0.0, clearWhenDone: true);
        }
        else
        {
            Label.Visibility = Visibility.Visible;
            SetLabel(_current, _statusText);
            AnimateLabelOpacity(1.0);
        }

        if (_current == IslandStatus.WaitingApproval) StartPillPulse();
    }

    private void RefreshDetail()
    {
        RebuildSessionTabs(); // 多会话切换行（仅 _sessions.Count>1 时显示）

        var s = _displayed;
        var todos = s?.Todos ?? Array.Empty<TodoItem>();
        int total = todos.Count;
        if (total == 0)
        {
            // 无 todo：整个任务清单块收起，卡片只剩元信息块
            TaskCard.Visibility = Visibility.Collapsed;
            TodoList.Children.Clear();
        }
        else
        {
            TaskCard.Visibility = Visibility.Visible;
            int done = 0;
            foreach (var t in todos)
                if (IsStatus(t, "completed")) done++;

            DetailProgress.Visibility = Visibility.Visible;
            DetailProgress.Inlines.Clear();
            AddIconRun(DetailProgress, Ic.List, $"已完成 {done}/{total}");
            if (done >= total) AddIconRun(DetailProgress, Ic.Done, "");
            RebuildTodoList(todos);
        }
        DetailCwd.Inlines.Clear();
        AddIconRun(DetailCwd, Ic.Folder, ShortCwd(s?.Cwd ?? ""));
        DetailMeta.Inlines.Clear();
        AddIconRun(DetailMeta, Lc.Cpu, s?.Model ?? "—", LucideFont);
        DetailMeta.Inlines.Add(new Run("    "));
        AddIconRun(DetailMeta, Ic.Bolt, s?.Effort ?? "—");
        DetailMeta.Inlines.Add(new Run("    "));
        AddIconRun(DetailMeta, Lc.Chart, s?.Context ?? "—", LucideFont);
    }

    // 只铺开“在做 + 待办”（completed 已折叠进进度计数）；超 MaxTodoRows 尾部“… 还有 X 条”。
    private void RebuildTodoList(IReadOnlyList<TodoItem> todos)
    {
        TodoList.Children.Clear();
        var pending = new List<TodoItem>();
        foreach (var t in todos)
            if (!IsStatus(t, "completed")) pending.Add(t);

        if (pending.Count == 0)
        {
            TodoList.Visibility = Visibility.Collapsed; // 全部完成：只留进度行的 ✓
            return;
        }

        TodoList.Visibility = Visibility.Visible;
        int shown = Math.Min(pending.Count, MaxTodoRows);
        for (int i = 0; i < shown; i++) TodoList.Children.Add(MakeTodoLine(pending[i]));
        int rest = pending.Count - shown;
        if (rest > 0) TodoList.Children.Add(MakeMoreLine(rest));
    }

    private TextBlock MakeTodoLine(TodoItem t)
    {
        var (glyph, brush, bold) = StyleFor(t.Status);
        bool ip = IsStatus(t, "in_progress");
        string text = ip && !string.IsNullOrWhiteSpace(t.ActiveForm)
            ? t.ActiveForm
            : (!string.IsNullOrWhiteSpace(t.Content) ? t.Content : t.ActiveForm);
        var tb = new TextBlock
        {
            Foreground = brush,
            FontSize = TodoFontSize,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            MaxWidth = CardContentWidth,
            Margin = new Thickness(0, TodoRowGap, 0, 0)
        };
        AddIconRun(tb, glyph, text);
        return tb;
    }

    private TextBlock MakeMoreLine(int rest) => new()
    {
        Text = $"… 还有 {rest} 条",
        Foreground = TodoPendingBrush,
        FontSize = TodoFontSize,
        TextTrimming = TextTrimming.CharacterEllipsis,
        TextAlignment = TextAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
        MaxWidth = CardContentWidth,
        Margin = new Thickness(0, TodoRowGap, 0, 0)
    };

    private static (string glyph, Brush brush, bool bold) StyleFor(string status)
    {
        if (string.Equals(status, "in_progress", StringComparison.OrdinalIgnoreCase)) return (Ic.Play, ThinkingBrush, true);
        if (string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase)) return (Ic.Done, TodoDoneBrush, false);
        return (Ic.Circle, TodoPendingBrush, false); // pending / 空 / 未知
    }

    private static bool IsStatus(TodoItem t, string status) =>
        string.Equals(t.Status, status, StringComparison.OrdinalIgnoreCase);

    // 详情卡目标高度：按未完成 todo 行数动态算（completed 已折叠进进度计数，不占行）。
    private double DetailTargetHeight()
    {
        double tabs = MultiSession ? SessionTabsHeight : 0; // 多会话切换行
        var todos = _displayed?.Todos ?? Array.Empty<TodoItem>();
        if (todos.Count == 0) return Math.Min(DetailHeightBase + tabs, DetailCanvasHeight); // 回落：目录 + meta 两行

        int notDone = 0;
        foreach (var t in todos)
            if (!IsStatus(t, "completed")) notDone++;

        int rows = Math.Min(notDone, MaxTodoRows) + (notDone > MaxTodoRows ? 1 : 0); // 含折叠行
        // 顶 + (切换行) + 任务块(padding + 进度行 + 清单上间距 + 清单行) + 卡间距 + 元信息块(padding + 两行) + 底
        double h = DetailChromeTop
                 + tabs
                 + (CardPadV * 2 + ProgressRowHeight + 2 + rows * TodoRowHeight)
                 + CardGap
                 + (CardPadV * 2 + CwdMetaBlockHeight)
                 + DetailBottomPad;
        return Math.Min(h, DetailCanvasHeight); // 双保险：永不超裁剪画布
    }

    private bool MultiSession => _sessions.Count > 1;

    // 后台读 transcript，回 UI 线程把模型/上下文和 Task 系统清单填进该会话快照（不阻塞事件处理）。
    private void LoadTranscriptAsync(Session s)
    {
        var path = s.TranscriptPath;
        if (string.IsNullOrEmpty(path)) return;
        Task.Run(() =>
        {
            // 文件长度未变 → transcript 无新内容，跳过全量读取（大会话每次几 MB 解析 + LOH 分配的元凶）。
            try
            {
                long len = new System.IO.FileInfo(path!).Length;
                if (len == s.LastTranscriptLen) return;
                s.LastTranscriptLen = len;
            }
            catch { }
            var snap = TranscriptReader.ReadLatest(path!);
            var taskSnap = TranscriptReader.ReadTaskSnapshot(path!);
            if (snap is not { } v)
            {
                // 仅在读取失败时留一行线索，正常路径不刷日志。
                try
                {
                    System.IO.File.AppendAllText(IngestServer.LogPath,
                        $"{DateTime.Now:HH:mm:ss} [transcript] 读取失败/无 usage  path={path}{Environment.NewLine}");
                }
                catch { }
            }
            Dispatcher.InvokeAsync(() =>
            {
                if (snap is { } latest)
                {
                    var model = PrettyModel(latest.Model) ?? s.Model;
                    var context = FormatContext(latest.UsedTokens, latest.ContextSize);
                    if (model != s.Model || context != s.Context)
                    {
                        s.Model = model;
                        s.Context = context;
                        // 成功读数留一行（仅数值变化时，避免刷屏）：补上“成功路径不可观测”的缺口，
                        // 与失败分支的“读取失败/无 usage”对称，验证时直接看日志即可。
                        try
                        {
                            System.IO.File.AppendAllText(IngestServer.LogPath,
                                $"{DateTime.Now:HH:mm:ss} [transcript] model={model} ctx={context}{Environment.NewLine}");
                        }
                        catch { }
                    }
                }

                if (taskSnap.HasTaskHistory && !TodosEqual(s.Todos, taskSnap.Todos))
                {
                    s.Todos = taskSnap.Todos;
                    s.CurrentTask = CurrentTaskFrom(taskSnap.Todos) ?? s.CurrentTask;
                    try
                    {
                        System.IO.File.AppendAllText(IngestServer.LogPath,
                            $"{DateTime.Now:HH:mm:ss} [transcript] tasks={taskSnap.Todos.Count}{Environment.NewLine}");
                    }
                    catch { }
                }

                if (ReferenceEquals(s, _displayed) && _hovering)
                {
                    RefreshDetail();
                    AnimatePill(DetailWidth, DetailTargetHeight(), 0.29);
                }
            });
        });
    }

    private static string? CurrentTaskFrom(IReadOnlyList<TodoItem> todos)
    {
        foreach (var t in todos)
            if (IsStatus(t, "in_progress"))
                return !string.IsNullOrWhiteSpace(t.ActiveForm) ? t.ActiveForm : t.Content;
        return null;
    }

    private static bool TodosEqual(IReadOnlyList<TodoItem> a, IReadOnlyList<TodoItem> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (a[i] != b[i]) return false;
        return true;
    }

    // claude-opus-4-8 → opus-4-8（去 vendor 前缀；display_name 仅 statusLine 有，这里只有 id）
    private static string? PrettyModel(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        return id.StartsWith("claude-", StringComparison.OrdinalIgnoreCase)
            ? id["claude-".Length..]
            : id;
    }

    // 仅显示已用 token：窗口大小无法从 transcript 可靠区分 200k/1M，百分比会误导，故不显示。
    private static string FormatContext(long used, int size)
    {
        double k = used / 1000.0;
        return k >= 100 ? $"{k:0}k" : $"{k:0.0}k";
    }

    private static string ShortCwd(string cwd)
    {
        if (string.IsNullOrEmpty(cwd)) return "—";
        var norm = cwd.Replace('\\', '/').TrimEnd('/');
        var segs = norm.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segs.Length == 0) return cwd;
        if (segs.Length == 1) return segs[^1];
        return ".../" + segs[^2] + "/" + segs[^1];
    }

    // 会话标签用：只取末段目录名（窄标签放不下完整路径，靠 ToolTip 补全路径）
    private static string LeafCwd(string cwd)
    {
        if (string.IsNullOrEmpty(cwd)) return "—";
        var segs = cwd.Replace('\\', '/').TrimEnd('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segs.Length == 0 ? cwd : segs[^1];
    }

    private double MeasureWidth(string text, bool hasIcon = false)
    {
        _measure.Text = text;
        _measure.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        // ambient（信息源接管）时 _current 仍是 Idle，靠 hasIcon 显式计入图标宽
        double iconW = (hasIcon || IconForStatus(_current) != null) ? _measure.FontSize * 1.2 : 0;
        return SidePadding * 2 + DotSize + LabelLeftGap + _measure.DesiredSize.Width + iconW + 2;
    }

    // —— 顶部水平居中（窗口尺寸固定；多屏 / DPI 留 M5） ——
    private void Reposition()
    {
        var wa = SystemParameters.WorkArea;
        Left = wa.Left + (wa.Width - Width) / 2;
        Top = wa.Top;
    }

    // 横条态宽度形变（弹簧回弹）
    private void AnimateWidth(double to, bool expanding)
    {
        EasingFunctionBase ease = expanding
            ? new SpringEase { EasingMode = EasingMode.EaseIn, DurationSeconds = 0.36, Response = 0.20, DampingFraction = 0.70 }
            : new SpringEase { EasingMode = EasingMode.EaseIn, DurationSeconds = 0.36, Response = 0.18, DampingFraction = 1.0 };
        var anim = new DoubleAnimation(to, TimeSpan.FromSeconds(0.36)) { EasingFunction = ease };
        Pill.BeginAnimation(WidthProperty, anim);
    }

    // 详情卡片：宽高同一弹簧同时长。展开略带 overshoot（SwiftUI 手感），收起临界阻尼不回弹
    private void AnimatePill(double w, double h, double secs, bool expanding = true)
    {
        EasingFunctionBase ease = new SpringEase
        {
            EasingMode = EasingMode.EaseIn,         // 透传弹簧曲线（勿用默认 EaseOut 反射）
            DurationSeconds = secs,
            Response = expanding ? 0.20 : 0.18,
            DampingFraction = expanding ? 0.72 : 1.0
        };
        Pill.BeginAnimation(WidthProperty,
            new DoubleAnimation(w, TimeSpan.FromSeconds(secs)) { EasingFunction = ease });
        Pill.BeginAnimation(HeightProperty,
            new DoubleAnimation(h, TimeSpan.FromSeconds(secs)) { EasingFunction = ease });
    }

    // —— squircle 圆角半径：展开态加大让连续曲率显威，胶囊态回 14 ——
    public double PillRadius
    {
        get => (double)GetValue(PillRadiusProperty);
        set => SetValue(PillRadiusProperty, value);
    }
    public static readonly DependencyProperty PillRadiusProperty =
        DependencyProperty.Register(nameof(PillRadius), typeof(double), typeof(IslandWindow),
            new PropertyMetadata(14.0, OnPillRadiusChanged));

    private static void OnPillRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var win = (IslandWindow)d;
        double radius = (double)e.NewValue;
        win.BgShape.Radius = radius;     // 背景层同步（AffectsRender 自动重画）
        win.UpdateClip(radius);          // 裁剪层逐帧重建
    }

    private void UpdateClip(double radius)
    {
        if (ClipHost.ActualWidth > 0 && ClipHost.ActualHeight > 0)
            ClipHost.Clip = Squircle.BuildGeometry(ClipHost.ActualWidth, ClipHost.ActualHeight, radius);
    }

    // 半径过渡：随展开/收起，少 overshoot（圆角不宜回弹过冲）
    private void AnimateRadius(double to, double secs)
    {
        var ease = new SpringEase { EasingMode = EasingMode.EaseIn, DurationSeconds = secs, Response = 0.20, DampingFraction = 0.9 };
        BeginAnimation(PillRadiusProperty, new DoubleAnimation(to, TimeSpan.FromSeconds(secs)) { EasingFunction = ease });
    }

    private void AnimateLabelOpacity(double to, bool clearWhenDone = false)
    {
        if (to > 0) Label.Visibility = Visibility.Visible;
        var anim = new DoubleAnimation(to, TimeSpan.FromSeconds(0.22));
        if (clearWhenDone)
            anim.Completed += (_, _) =>
            {
                if (_current == IslandStatus.Idle && !_hovering)
                {
                    Label.Text = "";
                    Label.Visibility = Visibility.Collapsed;
                }
            };
        Label.BeginAnimation(OpacityProperty, anim);
    }

    private void AnimateBg(Color to)
    {
        _bgBrush.BeginAnimation(SolidColorBrush.ColorProperty,
            new ColorAnimation(to, TimeSpan.FromSeconds(0.18)));
    }

    private static void BeginDouble(UIElement el, DependencyProperty prop, double to, double secs, Action? completed = null)
    {
        var anim = new DoubleAnimation(to, TimeSpan.FromSeconds(secs));
        if (completed != null) anim.Completed += (_, _) => completed();
        el.BeginAnimation(prop, anim);
    }

    // —— 圆点呼吸 / 脉冲 ——
    private void StartDotPulse(bool gentle)
    {
        var anim = new DoubleAnimation(1.0, gentle ? 0.4 : 0.15,
            TimeSpan.FromSeconds(gentle ? 0.9 : 0.55))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        Dot.BeginAnimation(OpacityProperty, anim);
    }

    private void StopDotPulse()
    {
        Dot.BeginAnimation(OpacityProperty, null);
        Dot.Opacity = 1.0;
    }

    // —— 等待批准时药丸轻微脉动 ——
    private void StartPillPulse()
    {
        var anim = new DoubleAnimation(1.0, 1.05, TimeSpan.FromSeconds(0.6))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        PillScale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        PillScale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }

    private void StopPillPulse()
    {
        PillScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        PillScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        PillScale.ScaleX = 1.0;
        PillScale.ScaleY = 1.0;
    }

    // —— 完成态绿色流光：一道辉光弧沿 squircle 轮廓绕 GlowLoops 圈后淡出，外发光 ——
    // 几何与单位按当前 Pill 尺寸 + 圆角实时算；仅 GlowPath 可见时更新（随 Done 宽度过渡跟随轮廓）。
    private void UpdateGlowGeometry()
    {
        double w = BgShape.ActualWidth, h = BgShape.ActualHeight;
        if (w <= 0 || h <= 0) return;
        GlowPath.Data = Squircle.BuildGeometry(w, h, PillRadius);
        _glowUnit = Squircle.Perimeter(w, h, PillRadius) / GlowPath.StrokeThickness; // dash/offset 以线宽为单位
        GlowPath.StrokeDashArray = new DoubleCollection
        {
            GlowArcFraction * _glowUnit,         // 亮弧段
            (1 - GlowArcFraction) * _glowUnit    // 其余透明
        };
    }

    private void StartGlow()
    {
        UpdateGlowGeometry();
        if (_glowUnit <= 0) return; // 尺寸未就绪：跳过，不留半截动画
        GlowPath.Visibility = Visibility.Visible;
        GlowPath.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0.0, 1.0, TimeSpan.FromSeconds(GlowFadeInSeconds)));
        // 亮弧沿轮廓绕 GlowLoops 圈：offset 负向滑动（顺时针观感），SineEase 两端略缓
        var spin = new DoubleAnimation(0.0, -GlowLoops * _glowUnit, TimeSpan.FromSeconds(GlowDurationSeconds))
        {
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        spin.Completed += (_, _) => { if (_current == IslandStatus.Done) EndGlow(); };
        GlowPath.BeginAnimation(Shape.StrokeDashOffsetProperty, spin);
    }

    // 绕圈跑完：淡出后收起并释放几何（仍在 Done 态，等 _doneTimer 回 Idle）
    private void EndGlow()
    {
        var fade = new DoubleAnimation(0.0, TimeSpan.FromSeconds(GlowFadeOutSeconds));
        fade.Completed += (_, _) =>
        {
            GlowPath.Visibility = Visibility.Collapsed;
            GlowPath.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
            GlowPath.Data = null;
        };
        GlowPath.BeginAnimation(OpacityProperty, fade);
    }

    // 被新状态打断（新 turn / 思考态等）立即收光：清动画与几何，幂等
    private void StopGlow()
    {
        if (GlowPath.Visibility == Visibility.Collapsed) return;
        GlowPath.BeginAnimation(OpacityProperty, null);
        GlowPath.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
        GlowPath.Opacity = 0.0;
        GlowPath.Visibility = Visibility.Collapsed;
        GlowPath.Data = null;
    }

    // —— 不抢焦点 / 不进任务栏与 Alt-Tab ——
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    private static SolidColorBrush MakeBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static SolidColorBrush MakeBrush2(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    // —— Win32 interop ——
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    // —— 空闲内存回收:大会话读 transcript 后私有内存会涨,会话回 Idle 时把堆与工作集还给 OS ——
    private DateTime _lastTrim = DateTime.MinValue;
    private void TrimWorkingSet()
    {
        var now = DateTime.Now;
        if ((now - _lastTrim).TotalSeconds < 20) return; // 节流,避免频繁 GC 暂停
        _lastTrim = now;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect();
        try { SetProcessWorkingSetSize(GetCurrentProcess(), (IntPtr)(-1), (IntPtr)(-1)); } catch { }
    }

    [DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr proc, IntPtr min, IntPtr max);
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
}
