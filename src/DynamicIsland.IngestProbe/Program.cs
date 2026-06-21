// M0 数据流探针
// 监听 127.0.0.1:7777，把 Claude Code / Codex 通过 hooks 发来的事件实时打印出来。
// 目的：在写任何 UI 之前，先亲眼确认“信息拿得到”。

using System.Net;
using System.Text;
using System.Text.Json;

const string Prefix = "http://127.0.0.1:7777/";

using var listener = new HttpListener();
listener.Prefixes.Add(Prefix);

try
{
    listener.Start();
}
catch (HttpListenerException ex)
{
    Console.WriteLine($"无法在 {Prefix} 启动监听：{ex.Message}");
    Console.WriteLine("端口可能被占用——换个端口，或关掉占用进程后重试。");
    return;
}

Console.WriteLine($"[IngestProbe] 正在监听 {Prefix}");
Console.WriteLine("去用 Claude Code / Codex 干点活，事件会实时打印在下面。按 Ctrl+C 退出。\n");

while (true)
{
    HttpListenerContext ctx;
    try { ctx = await listener.GetContextAsync(); }
    catch { break; }
    _ = HandleAsync(ctx); // fire-and-forget，允许多个 hook 并发上报
}

static async Task HandleAsync(HttpListenerContext ctx)
{
    try
    {
        var req = ctx.Request;
        string body;
        using (var reader = new StreamReader(req.InputStream, req.ContentEncoding))
            body = await reader.ReadToEndAsync();

        Print(req.Url?.AbsolutePath ?? "/", body);

        // 始终返回合法 JSON “{}”：满足 Codex `Stop` hook 对 JSON 输出的要求，对 Claude 也无害。
        var buf = Encoding.UTF8.GetBytes("{}");
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "application/json";
        ctx.Response.ContentLength64 = buf.Length;
        await ctx.Response.OutputStream.WriteAsync(buf);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[error] {ex.Message}");
    }
    finally
    {
        try { ctx.Response.Close(); } catch { }
    }
}

static void Print(string path, string body)
{
    var ts = DateTime.Now.ToString("HH:mm:ss");
    var source = path.StartsWith("/codex", StringComparison.OrdinalIgnoreCase) ? "codex" : "claude";
    var isStatus = path.Contains("status", StringComparison.OrdinalIgnoreCase);

    string line;
    try
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        // 按路径逐层取值；任意一层缺失返回空字符串
        string Get(params string[] keys)
        {
            var e = root;
            foreach (var k in keys)
            {
                if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(k, out var next))
                    return "";
                e = next;
            }
            return e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : e.ToString();
        }

        if (isStatus)
        {
            line = $"status   model={Get("model", "display_name")}  " +
                   $"cost=${Get("cost", "total_cost_usd")}  " +
                   $"ctx={Get("context_window", "used_percentage")}%";
        }
        else
        {
            var sb = new StringBuilder($"event={Get("hook_event_name")}");
            var tool = Get("tool_name");
            var sid = Get("session_id");
            var cwd = Get("cwd");
            if (tool.Length > 0) sb.Append($"  tool={tool}");
            if (sid.Length > 0) sb.Append($"  session={(sid.Length <= 8 ? sid : sid[..8])}");
            if (cwd.Length > 0) sb.Append($"  cwd={cwd}");
            line = sb.ToString();
        }
    }
    catch
    {
        // 非 JSON：原样打印（截断），便于排查
        line = body.Length > 200 ? body[..200] + "…" : body;
    }

    Console.WriteLine($"{ts}  [{source,-6}]  {line}");
}
