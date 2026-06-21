using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using DynamicIsland.Core.Model;

namespace DynamicIsland.Core.Ingest;

/// <summary>
/// 本地 HTTP 接收端：监听 127.0.0.1:7777，接收 Claude / Codex hooks 经 curl POST 来的事件 JSON，
/// 解析归一化后通过 <see cref="EventReceived"/> 抛出，并追加一行到临时日志。对所有请求一律回 "{}"
/// （兼容 Codex 的 Stop 事件必须输出合法 JSON 的要求）。
///
/// 不引用任何 WPF 类型，便于日后整体搬进 DynamicIsland.Core 做单元测试。
/// </summary>
public sealed class IngestServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly string _prefix;
    private CancellationTokenSource? _cts;

    /// <summary>事件日志路径，便于调试与“不靠肉眼”确认事件确实到达。</summary>
    public static readonly string LogPath =
        Path.Combine(Path.GetTempPath(), "dynamicisland-events.log");
    private static readonly object _logLock = new();

    /// <summary>收到一条事件时触发。注意：在后台线程上抛出，订阅方需自行切回 UI 线程。</summary>
    public event Action<IngestEvent>? EventReceived;

    public IngestServer(int port = 7777)
    {
        _prefix = $"http://127.0.0.1:{port}/";
        _listener.Prefixes.Add(_prefix);
    }

    /// <summary>启动监听。端口被占用会抛 <see cref="HttpListenerException"/>。</summary>
    public void Start()
    {
        _listener.Start();
        _cts = new CancellationTokenSource();
        _ = AcceptLoopAsync(_cts.Token);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { break; } // listener 已停止
            _ = HandleAsync(ctx);
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            var req = ctx.Request;
            string body;
            using (var reader = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8))
                body = await reader.ReadToEndAsync();

            var source = (req.Url?.AbsolutePath ?? "/")
                .StartsWith("/codex", StringComparison.OrdinalIgnoreCase) ? "codex" : "claude";

            var evt = Parse(source, body);
            if (evt is not null)
            {
                Log(evt);
                try { EventReceived?.Invoke(evt); }
                catch { /* 订阅方异常不应拖垮接收端 */ }
            }

            var buf = Encoding.UTF8.GetBytes("{}");
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = buf.Length;
            await ctx.Response.OutputStream.WriteAsync(buf);
        }
        catch { /* 单个请求失败忽略，保持服务存活 */ }
        finally { try { ctx.Response.Close(); } catch { } }
    }

    private static IngestEvent? Parse(string source, string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            string? Str(string key) =>
                root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
                    ? v.GetString()
                    : null;

            // 取嵌套字符串，如 effort.level
            string? Nested(string k1, string k2) =>
                root.TryGetProperty(k1, out var o) && o.ValueKind == JsonValueKind.Object
                && o.TryGetProperty(k2, out var v) && v.ValueKind == JsonValueKind.String
                    ? v.GetString()
                    : null;

            var todos = ExtractTodos(root, Str("tool_name"));
            return new IngestEvent(
                Source: source,
                EventName: Str("hook_event_name") ?? "",
                Tool: Str("tool_name"),
                SessionId: Str("session_id"),
                Cwd: Str("cwd"),
                Effort: Nested("effort", "level"),
                TranscriptPath: Str("transcript_path"),
                CurrentTask: CurrentTaskFrom(todos),
                Todos: todos,
                Message: Str("message"));
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// TodoWrite 事件里提取全量待办（按 JSON 原序），供详情卡渲染多行清单 + 进度。
    /// 非 TodoWrite / 无 todos 数组 / 空列表 → null（下游据此判断“本事件不带 todos”，不覆盖已存清单）。
    /// </summary>
    private static IReadOnlyList<TodoItem>? ExtractTodos(JsonElement root, string? toolName)
    {
        if (!string.Equals(toolName, "TodoWrite", StringComparison.OrdinalIgnoreCase)) return null;
        if (!root.TryGetProperty("tool_input", out var ti) || ti.ValueKind != JsonValueKind.Object) return null;
        if (!ti.TryGetProperty("todos", out var arr) || arr.ValueKind != JsonValueKind.Array) return null;

        var list = new List<TodoItem>();
        foreach (var todo in arr.EnumerateArray())
        {
            if (todo.ValueKind != JsonValueKind.Object) continue;

            string S(string k) =>
                todo.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : "";

            var content = S("content");
            var active = S("activeForm");
            var status = S("status");
            if (content.Length == 0 && active.Length == 0) continue; // 跳过 content/activeForm 皆空的脏项
            list.Add(new TodoItem(content, active, status));
        }
        return list.Count == 0 ? null : list;
    }

    /// <summary>取首个 in_progress 项的 activeForm（无则 content），作为“当前任务”——供日志与兜底用。</summary>
    private static string? CurrentTaskFrom(IReadOnlyList<TodoItem>? todos)
    {
        if (todos is null) return null;
        foreach (var t in todos)
            if (string.Equals(t.Status, "in_progress", StringComparison.OrdinalIgnoreCase))
                return !string.IsNullOrWhiteSpace(t.ActiveForm) ? t.ActiveForm : t.Content;
        return null;
    }

    /// <summary>把事件追加到临时日志（失败静默，绝不影响接收）。</summary>
    private static void Log(IngestEvent e)
    {
        try
        {
            var sid = string.IsNullOrEmpty(e.SessionId) ? "" : $" session={Short(e.SessionId!)}";
            var tool = e.Tool is null ? "" : $" tool={e.Tool}";
            var eff = e.Effort is null ? "" : $" effort={e.Effort}";
            var task = string.IsNullOrEmpty(e.CurrentTask) ? "" : $" task=\"{e.CurrentTask}\"";
            var msg = string.IsNullOrEmpty(e.Message) ? "" : $" msg=\"{e.Message}\"";
            var line = $"{DateTime.Now:HH:mm:ss} [{e.Source}] {e.EventName}{tool}{eff}{task}{msg}{sid}{Environment.NewLine}";
            lock (_logLock) File.AppendAllText(LogPath, line);
        }
        catch { /* 日志失败不影响主流程 */ }
    }

    private static string Short(string s) => s.Length <= 8 ? s : s[..8];

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { if (_listener.IsListening) _listener.Stop(); } catch { }
        try { _listener.Close(); } catch { }
    }
}
