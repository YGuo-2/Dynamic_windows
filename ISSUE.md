# ISSUE：详情卡 todo list 在真实会话里始终为空

> 状态：**已修复** · 发现日期 2026-06-19 · 修复日期 2026-06-19 · 影响 M2 详情卡「todo 清单 + 进度」
> 这是真实端到端从未跑通的功能，不是代码退化——见「为什么上次修复是假象」。

---

## 现象

灵动岛 hover 详情卡里的「todo 清单 + 进度」，在**真实 Claude Code 会话**里始终空白；只有用 `curl` 手工灌 `TodoWrite` 测试事件时才显示。

## 根因（一句话）

当前这台机器的 Claude Code 用 **`TaskCreate` / `TaskUpdate` / `TaskList`** 这套任务系统管理待办，**根本不存在 `TodoWrite` 工具**；而采集层 `Ingest/IngestServer.cs` 的 `ExtractTodos`（约 127 行）只认 `tool_name == "TodoWrite"`，于是真实会话**永远不产生可被采集的 todo 事件** → 详情卡 list 恒空。

## 证据

1. **本会话工具调用统计**（从 transcript 解析）：`TaskCreate ×15`、`TaskUpdate ×31`、`TaskList ×1`，**`TodoWrite ×0`**。
2. **落盘位置**：
   - `~/.claude/todos/`（经典 TodoWrite 落盘处）**空**。
   - `~/.claude/tasks/session-383383e1/` 只有 `.highwatermark`（2 字节计数）和 `.lock`，**没有全量任务 JSON** → 「读持久化文件」这条路不通。
3. **payload 结构是增量、非全量**：
   - `TaskCreate` input = `{subject, description, activeForm}` —— **无 id、无 status**（id 在 tool_response `"Task #N created..."` 文本里，status 初始默认 pending）。
   - `TaskUpdate` input = `{taskId, status}` —— 只改单个任务。
   - 不像 `TodoWrite` 一次给整个 `todos[]` 全量数组，采集层没法像现在这样「一把梭覆盖」。
4. `TaskList` 在任务 completed 后返回 `No tasks found`，**也不是稳定的全量源**。

## 为什么上次「修复」是假象

上次端到端验证用 `curl` 灌的是**手工构造的 TodoWrite hook JSON**，只验证了 UI 渲染逻辑；真实工具根本不发 TodoWrite，那条代码路径在真实会话里**没有数据流过**。「UserPromptSubmit 清空 bug」是 TodoWrite 路径内的真问题，但该路径在现实中是死的。**这是测试盲区。**

---

## 推荐方案 A：从 transcript 重建任务清单（已 PoC 验证）

项目**已经在读同一个 transcript**（`Ingest/TranscriptReader.cs` 取 `message.model` / `message.usage`），数据源天然统一。transcript 里有完整的 `TaskCreate` / `TaskUpdate` 历史，可完整重建任务清单 + 进度。

### 算法

```
遍历 transcript JSONL，维护 uses[tool_use_id] = (name, input)：
  • tool_use 且 name == "TaskUpdate"：按 input.taskId 改 status；
      status == "deleted" 则从表中删除该任务
  • tool_result：用 tool_use_id 找回配对的 TaskCreate，
      从 result 文本正则 #(\d+) 取 id，从 input 取 subject / activeForm，
      建 task{ content: subject, activeForm, status: "pending" }
文件顺序即时间序，保证 create 先于 update。
结果：taskId → { content, activeForm, status }
```

> ⚠️ **关键坑点**：`TaskCreate` 的任务 **id 在 tool_result，不在 tool_input**——只看 input 会找不到 id，无法和后续 `TaskUpdate` 关联。

### PoC（Python，已实测可跑）

```python
import json, re
p = r"C:\Users\ny\.claude\projects\E--CodeProject-Dynamic-windows\<session>.jsonl"
uses = {}; tasks = {}; order = []
def text_of(rc):
    if isinstance(rc, str): return rc
    if isinstance(rc, list): return " ".join(x.get("text","") for x in rc if isinstance(x, dict))
    return ""
with open(p, encoding="utf-8", errors="ignore") as f:
    for line in f:
        try: o = json.loads(line)
        except: continue
        for b in (o.get("message") or {}).get("content") or []:
            if not isinstance(b, dict): continue
            t = b.get("type")
            if t == "tool_use":
                uses[b.get("id")] = (b.get("name"), b.get("input") or {})
                if b.get("name") == "TaskUpdate":
                    inp = b.get("input") or {}; tid = str(inp.get("taskId","")); st = inp.get("status")
                    if tid in tasks and st:
                        tasks.pop(tid, None) if st == "deleted" else tasks[tid].__setitem__("status", st)
            elif t == "tool_result":
                uid = b.get("tool_use_id")
                if uid not in uses: continue
                name, inp = uses[uid]
                if name == "TaskCreate":
                    m = re.search(r"#(\d+)", text_of(b.get("content")))
                    if m:
                        tid = m.group(1)
                        tasks[tid] = {"content": inp.get("subject",""), "activeForm": inp.get("activeForm",""), "status": "pending"}
                        if tid not in order: order.append(tid)
done = sum(1 for t in tasks.values() if t["status"] == "completed")
print(f"进度 已完成 {done}/{len(tasks)}")
for tid in order:
    if tid in tasks: print(f"  #{tid} [{tasks[tid]['status']}] {tasks[tid]['content']}")
```

**实测输出**（本会话）：完整还原 15 个任务 + 状态 + `进度 已完成 15/15`，content / status 全部正确。

### 落地改动（都在现有结构内）

| 文件 | 改动 |
|---|---|
| `Ingest/TranscriptReader.cs` | 新增 `ReadTasks(path)`：全文扫描按上述算法重建任务，返回 `IReadOnlyList<TodoItem>`（字段完全复用） |
| `IslandWindow.xaml.cs` `LoadTranscriptAsync`（约 355 行） | 后台读 transcript 时顺带 `ReadTasks`，填 `Session.Todos`（约 74 行） |
| 触发时机 | **任何事件**都重建（不再依赖 TodoWrite）——顺带根治「不调待办工具的轮次不更新」 |

- `Model/TodoItem.cs` 直接复用：Task 的 `status` 取值 `pending / in_progress / completed` 与 `TodoItem` 一致，`StyleFor` / `RebuildTodoList` 无需改；`deleted` 跳过。
- **现有渲染天然契合**：completed 折叠进 `📋 已完成 X/Y`，只铺开未完成几条——即便重建出整个会话全量任务，UI 也只显示未完成 + 进度计数，不会刷屏。

### 两个注意点

1. **性能**：当前 `ReadLatest` 只读尾部 256KB；任务重建需**全文扫描**（`TaskCreate` 可能在很早）。首版可后台全文扫；transcript 很大时优化为「记录上次文件长度 + 增量读 + 缓存任务表」。
2. **向后兼容**：**保留** `ExtractTodos` 的 TodoWrite 路径，新增 Task 路径，以防别的版本 / 会话仍用 TodoWrite。

---

## 备选方案 B：纯 hook 增量（更轻量，但有前提）

`PostToolUse` 的 payload 带 `tool_response`（Claude Code 规范应有，**需 codex 临时 log 一次确认**）：
- `TaskCreate` 的 PostToolUse 同时带 `tool_input`（subject / activeForm）和 `tool_response`（`#N`）→ 拿到 id + 内容。
- `TaskUpdate` 的 `tool_input` 带 status。
- App 维护 `taskId → task` 内存字典增量更新。

优点：实时、不读大文件。缺点：要跨事件维护状态、依赖 `tool_response` 存在、`IngestServer` 当前未解析 `tool_response`。**transcript 方案（A）更稳、无状态，优先。**

---

## 验证方式（别再用 curl 模拟）

修复后，直接在真实会话里 `TaskCreate` / `TaskUpdate` 几个任务，hover 看 list 是否实时反映 + 进度正确。**用真实 Task 工具验证，不要用 curl 灌 TodoWrite**——那正是上次的盲区。

## 修复结果

- `Ingest/TranscriptReader.cs` 新增 transcript Task 重建逻辑：全文扫描 `TaskCreate` / `TaskUpdate`，从 `TaskCreate` 配对 `tool_result` 的 `Task #N created...` 提取 id，再用 `TaskUpdate.taskId/status` 更新任务状态。
- `IslandWindow.xaml.cs` 的 `LoadTranscriptAsync` 现在同时刷新模型/上下文和任务清单；真实 Task 历史存在时覆盖 `Session.Todos`，旧 `TodoWrite` 路径仍保留兼容。
- `CLAUDE.md` 与 `DESIGN.md` 已同步更新数据来源，明确真实会话主路径不是 `TodoWrite`。

## 修复后验证

- `dotnet build .\DynamicIsland.slnx -c Debug -v minimal -nologo` 通过。
- 用真实会话 transcript 触发 App 读取，日志出现 `[transcript] tasks=15`，确认走的是 Task 系统 transcript 重建路径，不是 `TodoWrite` curl 模拟路径。
- 最终验收仍应在真实 Claude Code 会话里创建 / 更新任务后 hover 查看进度实时变化。

---

## 附：本机相关事实

- transcript 路径示例：`~/.claude/projects/E--CodeProject-Dynamic-windows/<session_id>.jsonl`
- `~/.claude/todos/` 空；`~/.claude/tasks/session-<id>/` 仅 `.highwatermark` + `.lock`（无全量数据）
- 可用任务工具：`TaskCreate` / `TaskUpdate` / `TaskList` / `TaskGet` / `TaskStop` / `TaskOutput`（**无 `TodoWrite`**）
- `TaskCreate` result 文本格式：`Task #N created successfully: <subject>`；`TaskUpdate` result：`Updated task #N status`
