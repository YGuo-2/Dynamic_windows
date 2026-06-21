using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using DynamicIsland.Core.Model;

namespace DynamicIsland.Core.Ingest;

/// <summary>
/// 读 Claude Code 会话 transcript（JSONL）末尾最近一条 assistant 记录，取当前模型与上下文占用。
/// 纯本地读文件，不经 statusLine —— 与 ccusage / claude-devtools 等开源工具同源：
/// 每条 assistant 记录形如 {"type":"assistant","message":{"model":"…","usage":{…}}}。
///
/// 已用上下文 = input_tokens + cache_creation_input_tokens + cache_read_input_tokens（不含 output），
/// 与 Claude Code statusLine 的 used_percentage 口径一致。
///
/// 不引用任何 WPF 类型，便于日后整体搬进 DynamicIsland.Core 做单元测试。
/// </summary>
public static class TranscriptReader
{
    /// <summary>从 transcript 读到的一份快照。</summary>
    /// <param name="Model">模型标识（message.model），如 claude-opus-4-8。</param>
    /// <param name="UsedTokens">已用上下文 token（input + cache_creation + cache_read）。</param>
    /// <param name="ContextSize">上下文窗口大小（按模型推断，默认 200k）。</param>
    public readonly record struct Snapshot(string? Model, long UsedTokens, int ContextSize);

    /// <summary>Task 系统清单快照；HasTaskHistory 用于区分“无任务”和“旧 TodoWrite 会话”。</summary>
    public readonly record struct TaskSnapshot(IReadOnlyList<TodoItem> Todos, bool HasTaskHistory);

    // 尾部回读窗口：足以覆盖最后若干条记录（单条 assistant 记录通常远小于此）。
    private const int TailBytes = 256 * 1024;
    private static readonly Regex TaskIdRegex = new(@"#(\d+)", RegexOptions.Compiled);

    private readonly record struct ToolUse(string Name, JsonElement Input);

    /// <summary>读末尾最近一条带 usage 的 assistant 记录；任何失败都返回 null（绝不抛）。</summary>
    public static Snapshot? ReadLatest(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;

            // 尾窗回退：先读末尾 256KB；若没找到 assistant+usage，按 4× 扩大窗口重读，直至命中
            // 或覆盖整个文件。诱因（真实会话实测）：transcript 末尾出现一条接近/超过窗口大小的
            // 超大记录（图片 base64、整文件读取）且正在写入时，尾窗会被它占满、容不下它之前的
            // usage 记录而读空——扩窗即可够到更早的 usage。正常情况首窗即命中，几乎零额外开销。
            const long MaxWindow = 4L * 1024 * 1024; // 上限 4MB：正常首窗即命中，尾部异常超大行也不至于把整个大文件读进内存
            for (long win = TailBytes; ; win *= 4)
            {
                if (ReadWindow(path, win, out bool coveredWholeFile) is { } snap) return snap;
                if (coveredWholeFile || win >= MaxWindow) return null;
            }
        }
        catch { return null; }
    }

    /// <summary>
    /// 从完整 transcript 重建 Claude Task 系统任务清单。真实会话当前使用 TaskCreate/TaskUpdate，
    /// 不是 TodoWrite；TaskCreate 的 task id 只出现在配对 tool_result 文本中。
    /// </summary>
    public static IReadOnlyList<TodoItem> ReadTasks(string path) => ReadTaskSnapshot(path).Todos;

    public static TaskSnapshot ReadTaskSnapshot(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return new TaskSnapshot(Array.Empty<TodoItem>(), HasTaskHistory: false);

            var uses = new Dictionary<string, ToolUse>();
            var tasks = new Dictionary<string, TodoItem>();
            var order = new List<string>();
            var hasTaskHistory = false;

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            while (sr.ReadLine() is { } line)
            {
                line = line.Trim();
                if (line.Length == 0 || line[0] != '{') continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (!TryGetContentBlocks(doc.RootElement, out var blocks)) continue;

                    foreach (var block in blocks.EnumerateArray())
                    {
                        if (block.ValueKind != JsonValueKind.Object) continue;
                        if (!block.TryGetProperty("type", out var typeEl)) continue;
                        var type = typeEl.GetString();

                        if (type == "tool_use")
                        {
                            hasTaskHistory |= TrackToolUse(block, uses, tasks);
                        }
                        else if (type == "tool_result")
                        {
                            hasTaskHistory |= ApplyTaskCreateResult(block, uses, tasks, order);
                        }
                    }
                }
                catch (JsonException)
                {
                    // Transcript may be written concurrently; skip incomplete/bad lines.
                }
            }

            var result = new List<TodoItem>();
            foreach (var taskId in order)
            {
                if (tasks.TryGetValue(taskId, out var task)) result.Add(task);
            }
            return new TaskSnapshot(result, hasTaskHistory);
        }
        catch
        {
            return new TaskSnapshot(Array.Empty<TodoItem>(), HasTaskHistory: false);
        }
    }

    /// <summary>读末尾 <paramref name="window"/> 字节，反向找最近一条带正 usage 的 assistant 记录。</summary>
    /// <param name="coveredWholeFile">本次窗口起点已到文件头（再扩大也无更多内容）。</param>
    private static Snapshot? ReadWindow(string path, long window, out bool coveredWholeFile)
    {
        // 共享读：claude 进程可能正在写，FileShare.ReadWrite 避免锁冲突。
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        long start = Math.Max(0, fs.Length - window);
        coveredWholeFile = start == 0;
        fs.Seek(start, SeekOrigin.Begin);
        using var sr = new StreamReader(fs);
        var text = sr.ReadToEnd();

        // 反向逐行找最后一条 assistant + usage。首行可能因截断而不完整，TryParse 会自然跳过。
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line[0] != '{') continue;
            if (!line.Contains("\"usage\"", StringComparison.Ordinal)) continue;
            if (TryParse(line) is { } snap) return snap;
        }
        return null;
    }

    private static Snapshot? TryParse(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("type", out var t) || t.GetString() != "assistant") return null;
            if (!root.TryGetProperty("message", out var m) || m.ValueKind != JsonValueKind.Object) return null;

            string? model = m.TryGetProperty("model", out var mo) && mo.ValueKind == JsonValueKind.String
                ? mo.GetString()
                : null;

            if (!m.TryGetProperty("usage", out var u) || u.ValueKind != JsonValueKind.Object) return null;

            long Tok(string k) =>
                u.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;

            long used = Tok("input_tokens") + Tok("cache_creation_input_tokens") + Tok("cache_read_input_tokens");
            if (used <= 0) return null;

            return new Snapshot(model, used, ContextSizeFor(model, used));
        }
        catch (JsonException) { return null; }
    }

    private static bool TryGetContentBlocks(JsonElement root, out JsonElement blocks)
    {
        blocks = default;
        return root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("message", out var message)
            && message.ValueKind == JsonValueKind.Object
            && message.TryGetProperty("content", out blocks)
            && blocks.ValueKind == JsonValueKind.Array;
    }

    private static bool TrackToolUse(
        JsonElement block,
        Dictionary<string, ToolUse> uses,
        Dictionary<string, TodoItem> tasks)
    {
        var id = StringProp(block, "id");
        var name = StringProp(block, "name");
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name)) return false;

        bool isCreate = string.Equals(name, "TaskCreate", StringComparison.OrdinalIgnoreCase);
        bool isUpdate = string.Equals(name, "TaskUpdate", StringComparison.OrdinalIgnoreCase);
        // 只有 Task 系统工具与任务清单相关；其它工具（Read/Bash/Edit…）的 input 一律不 clone 不存，
        // 否则大会话几百个 tool_use 的大 input 克隆全驻留字典 → hover 单次读取私有内存飙到 200MB+（实测主因）。
        if (!isCreate && !isUpdate) return false;
        if (!block.TryGetProperty("input", out var inputEl) || inputEl.ValueKind != JsonValueKind.Object) return true;

        if (isCreate)
        {
            // 仅 TaskCreate 需留存 input：其 taskId 在配对 tool_result 文本里，要回头取 subject/activeForm。
            uses[id] = new ToolUse(name, inputEl.Clone());
            return true;
        }

        // TaskUpdate 当场结算（不靠 tool_result 配对），直接读原始 inputEl，无需 clone 留存。
        var taskId = PropAsString(inputEl, "taskId");
        var status = StringProp(inputEl, "status");
        if (string.IsNullOrWhiteSpace(taskId) || string.IsNullOrWhiteSpace(status)) return true;

        if (string.Equals(status, "deleted", StringComparison.OrdinalIgnoreCase))
        {
            tasks.Remove(taskId);
        }
        else if (tasks.TryGetValue(taskId, out var existing))
        {
            tasks[taskId] = existing with { Status = status };
        }
        return true;
    }

    private static bool ApplyTaskCreateResult(
        JsonElement block,
        Dictionary<string, ToolUse> uses,
        Dictionary<string, TodoItem> tasks,
        List<string> order)
    {
        var toolUseId = StringProp(block, "tool_use_id");
        if (string.IsNullOrEmpty(toolUseId)) return false;
        if (!uses.TryGetValue(toolUseId, out var use)) return false;
        if (!string.Equals(use.Name, "TaskCreate", StringComparison.OrdinalIgnoreCase)) return false;

        var resultText = block.TryGetProperty("content", out var content) ? TextOf(content) : "";
        var match = TaskIdRegex.Match(resultText);
        if (!match.Success) return true;

        var taskId = match.Groups[1].Value;
        var contentText = StringProp(use.Input, "subject");
        var activeForm = StringProp(use.Input, "activeForm");
        if (string.IsNullOrWhiteSpace(contentText) && string.IsNullOrWhiteSpace(activeForm)) return true;

        tasks[taskId] = new TodoItem(contentText, activeForm, "pending");
        if (!order.Contains(taskId)) order.Add(taskId);
        return true;
    }

    private static string TextOf(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String) return element.GetString() ?? "";
        if (element.ValueKind == JsonValueKind.Object) return StringProp(element, "text");
        if (element.ValueKind != JsonValueKind.Array) return "";

        var parts = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var s = item.GetString();
                if (!string.IsNullOrEmpty(s)) parts.Add(s);
            }
            else if (item.ValueKind == JsonValueKind.Object)
            {
                var s = StringProp(item, "text");
                if (!string.IsNullOrEmpty(s)) parts.Add(s);
            }
        }
        return string.Join(" ", parts);
    }

    private static string StringProp(JsonElement element, string key) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(key, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static string PropAsString(JsonElement element, string key)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(key, out var value)) return "";
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.TryGetInt64(out var n) ? n.ToString() : value.GetRawText(),
            _ => ""
        };
    }

    /// <summary>
    /// 推断上下文窗口大小。transcript 的 model 不带 [1m] 标记，无法直接区分 200k / 1M，故：
    /// ① model 显式带 1m → 1M；② 已用已超 200k（不可能超过窗口）→ 必是 1M；③ 否则按 200k。
    /// 对绝大多数被监控会话（标准 200k）准确；1M 会话用量未过 200k 时百分比会偏高，但 token 数始终真实。
    /// </summary>
    private static int ContextSizeFor(string? model, long used)
    {
        if (!string.IsNullOrEmpty(model)
            && (model.Contains("[1m]", StringComparison.OrdinalIgnoreCase)
                || model.Contains("-1m", StringComparison.OrdinalIgnoreCase))) return 1_000_000;
        if (used > 200_000) return 1_000_000;
        return 200_000;
    }
}
