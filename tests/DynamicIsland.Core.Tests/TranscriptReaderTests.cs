using System.Text.Json;
using DynamicIsland.Core.Ingest;

namespace DynamicIsland.Core.Tests;

public class TranscriptReaderTests
{
    [Fact]
    public void ReadLatestExpandsTailWindowWhenHugeTrailingLineHidesUsage()
    {
        WithTempFile(path =>
        {
            File.WriteAllText(
                path,
                AssistantUsageLine(123_000)
                + Environment.NewLine
                + NonUsageHugeLine(300_000)
                + Environment.NewLine);

            var snap = TranscriptReader.ReadLatest(path);

            Assert.NotNull(snap);
            Assert.Equal("claude-opus-4-8", snap.Value.Model);
            Assert.Equal(123_005, snap.Value.UsedTokens);
        });
    }

    [Fact]
    public void ReadTaskSnapshotReconstructsCreatesUpdatesDeletesAndNumericTaskId()
    {
        WithTempFile(path =>
        {
            File.WriteAllLines(path, new[]
            {
                TaskCreateLine("u1", "1", "Draft tests", "Drafting tests"),
                TaskCreateLine("u2", "2", "Delete me", "Deleting"),
                TaskUpdateLine("u3", "1", "in_progress"),
                TaskUpdateLine("u4", "2", "deleted", numericTaskId: true)
            });

            var snap = TranscriptReader.ReadTaskSnapshot(path);

            Assert.True(snap.HasTaskHistory);
            var task = Assert.Single(snap.Todos);
            Assert.Equal("Draft tests", task.Content);
            Assert.Equal("Drafting tests", task.ActiveForm);
            Assert.Equal("in_progress", task.Status);
        });
    }

    [Fact]
    public void IncrementalReadParsesOnlyAppendedTaskHistory()
    {
        WithTempFile(path =>
        {
            File.WriteAllText(path, TaskCreateLine("u1", "1", "Wire state", "Wiring state") + Environment.NewLine);
            var first = TranscriptReader.ReadTaskSnapshotIncremental(path, cursor: null);

            File.AppendAllText(path, TaskUpdateLine("u2", "1", "in_progress") + Environment.NewLine);
            var second = TranscriptReader.ReadTaskSnapshotIncremental(path, first.Cursor);

            Assert.True(second.Cursor.Offset > first.Cursor.Offset);
            var task = Assert.Single(second.Todos);
            Assert.Equal("in_progress", task.Status);
        });
    }

    [Fact]
    public void IncrementalReadDoesNotAdvancePastIncompleteTailLine()
    {
        WithTempFile(path =>
        {
            var line = TaskCreateLine("u1", "1", "Half line", "Completing half line");
            var split = line.Length / 2;
            File.WriteAllText(path, line[..split]);

            var first = TranscriptReader.ReadTaskSnapshotIncremental(path, cursor: null);
            Assert.Empty(first.Todos);
            Assert.Equal(0, first.Cursor.Offset);

            File.AppendAllText(path, line[split..] + Environment.NewLine);
            var second = TranscriptReader.ReadTaskSnapshotIncremental(path, first.Cursor);

            var task = Assert.Single(second.Todos);
            Assert.Equal("Half line", task.Content);
            Assert.True(second.Cursor.Offset > 0);
        });
    }

    [Fact]
    public void IncrementalReadResetsCursorWhenFileIsTruncated()
    {
        WithTempFile(path =>
        {
            File.WriteAllText(
                path,
                TaskCreateLine("u1", "1", new string('a', 1_000), "Long task") + Environment.NewLine);
            var first = TranscriptReader.ReadTaskSnapshotIncremental(path, cursor: null);
            Assert.NotEmpty(first.Todos);

            File.WriteAllText(path, TaskCreateLine("u2", "2", "Short task", "Shorting") + Environment.NewLine);
            var second = TranscriptReader.ReadTaskSnapshotIncremental(path, first.Cursor);

            var task = Assert.Single(second.Todos);
            Assert.Equal("Short task", task.Content);
            Assert.True(second.Cursor.Offset > 0);
            Assert.True(second.Cursor.Offset < first.Cursor.Offset);
        });
    }

    [Fact]
    public void ReadLatestReturnsNullForIncompleteUsageLine()
    {
        WithTempFile(path =>
        {
            var line = AssistantUsageLine(1000);
            File.WriteAllText(path, line[..20]);

            Assert.Null(TranscriptReader.ReadLatest(path));
        });
    }

    private static void WithTempFile(Action<string> action)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dynamicisland-test-{Guid.NewGuid():N}.jsonl");
        try
        {
            action(path);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    private static string AssistantUsageLine(long inputTokens, string model = "claude-opus-4-8") =>
        "{\"type\":\"assistant\",\"message\":{\"model\":" + Json(model)
        + ",\"usage\":{\"input_tokens\":" + inputTokens
        + ",\"cache_creation_input_tokens\":2,\"cache_read_input_tokens\":3}}}";

    private static string NonUsageHugeLine(int length) =>
        "{\"message\":{\"content\":[{\"type\":\"text\",\"text\":"
        + Json(new string('x', length))
        + "}]}}";

    private static string TaskCreateLine(string useId, string taskId, string subject, string activeForm) =>
        Content(
            "{\"type\":\"tool_use\",\"id\":" + Json(useId)
            + ",\"name\":\"TaskCreate\",\"input\":{\"subject\":" + Json(subject)
            + ",\"activeForm\":" + Json(activeForm) + "}}",
            "{\"type\":\"tool_result\",\"tool_use_id\":" + Json(useId)
            + ",\"content\":" + Json($"Task #{taskId} created successfully: {subject}") + "}");

    private static string TaskUpdateLine(string useId, string taskId, string status, bool numericTaskId = false)
    {
        var taskIdJson = numericTaskId ? taskId : Json(taskId);
        return Content(
            "{\"type\":\"tool_use\",\"id\":" + Json(useId)
            + ",\"name\":\"TaskUpdate\",\"input\":{\"taskId\":" + taskIdJson
            + ",\"status\":" + Json(status) + "}}");
    }

    private static string Content(params string[] blocks) =>
        "{\"message\":{\"content\":[" + string.Join(",", blocks) + "]}}";

    private static string Json(string value) => JsonSerializer.Serialize(value);
}
