using DynamicIsland.Core.Ingest;
using DynamicIsland.Core.Model;
using DynamicIsland.Core.State;

namespace DynamicIsland.Core.Tests;

public class AgentSessionStateMachineTests
{
    [Fact]
    public void LatePermissionNotificationAfterStopDoesNotReopenWaitingApproval()
    {
        var state = new AgentSessionState();
        var now = new DateTime(2026, 6, 22, 12, 0, 0);

        AgentSessionStateMachine.ApplyEvent(state, Event("UserPromptSubmit"), now);
        AgentSessionStateMachine.ApplyEvent(state, Event("Stop"), now.AddSeconds(1));
        AgentSessionStateMachine.ApplyEvent(
            state,
            Event("Notification", message: "Claude needs your permission to use Bash"),
            now.AddSeconds(2));

        Assert.Equal(IslandStatus.Done, state.Status);
        Assert.False(state.TurnRunning);
    }

    [Fact]
    public void IdleNotificationDoesNotStealCurrentState()
    {
        var state = new AgentSessionState();
        var now = new DateTime(2026, 6, 22, 12, 0, 0);

        AgentSessionStateMachine.ApplyEvent(state, Event("UserPromptSubmit"), now);
        AgentSessionStateMachine.ApplyEvent(state, Event("Stop"), now.AddSeconds(1));
        AgentSessionStateMachine.ApplyEvent(
            state,
            Event("Notification", message: "Claude is waiting for your input"),
            now.AddSeconds(2));

        Assert.Equal(IslandStatus.Done, state.Status);
    }

    [Fact]
    public void PermissionNotificationOnlyAppliesWhileTurnIsRunning()
    {
        var state = new AgentSessionState();
        var now = new DateTime(2026, 6, 22, 12, 0, 0);

        AgentSessionStateMachine.ApplyEvent(
            state,
            Event("Notification", message: "Claude needs approval"),
            now);
        Assert.Equal(IslandStatus.Idle, state.Status);

        AgentSessionStateMachine.ApplyEvent(state, Event("UserPromptSubmit"), now.AddSeconds(1));
        AgentSessionStateMachine.ApplyEvent(
            state,
            Event("Notification", message: "Claude needs approval"),
            now.AddSeconds(2));

        Assert.Equal(IslandStatus.WaitingApproval, state.Status);
    }

    [Fact]
    public void ToolHoldCanElapseAfterOtherSessionEvents()
    {
        var a = new AgentSessionState();
        var b = new AgentSessionState();
        var now = new DateTime(2026, 6, 22, 12, 0, 0);

        AgentSessionStateMachine.ApplyEvent(a, Event("UserPromptSubmit"), now);
        AgentSessionStateMachine.ApplyEvent(a, Event("PreToolUse", tool: "Bash"), now.AddMilliseconds(100));
        var result = AgentSessionStateMachine.ApplyEvent(a, Event("PostToolUse", tool: "Bash"), now.AddMilliseconds(200));
        AgentSessionStateMachine.ApplyEvent(b, Event("UserPromptSubmit"), now.AddMilliseconds(300));

        Assert.True(result.ArmToolHold);
        Assert.Equal(IslandStatus.RunningTool, a.Status);
        Assert.True(AgentSessionStateMachine.ApplyToolHoldElapsed(a));
        Assert.Equal(IslandStatus.Thinking, a.Status);
    }

    [Fact]
    public void ConsecutiveToolEventsUpdateRunningToolText()
    {
        var state = new AgentSessionState();
        var now = new DateTime(2026, 6, 22, 12, 0, 0);

        AgentSessionStateMachine.ApplyEvent(state, Event("PreToolUse", tool: "Bash"), now);
        AgentSessionStateMachine.ApplyEvent(state, Event("PreToolUse", tool: "Read"), now.AddMilliseconds(100));

        Assert.Equal(IslandStatus.RunningTool, state.Status);
        Assert.Equal("Read", state.Tool);
        Assert.Equal("Read", state.StatusText);
    }

    [Theory]
    [InlineData(IslandStatus.Thinking)]
    [InlineData(IslandStatus.RunningTool)]
    public void ActiveSilentSessionFallsBackToIdle(IslandStatus status)
    {
        var state = new AgentSessionState
        {
            LastActivity = new DateTime(2026, 6, 22, 12, 0, 0),
            Status = status,
            StatusText = status.ToString(),
            TurnRunning = true
        };

        var result = AgentSessionStateMachine.Prune(
            state,
            state.LastActivity + AgentSessionStateMachine.ActiveSilenceEvict + TimeSpan.FromSeconds(1));

        Assert.True(result.Changed);
        Assert.False(result.Remove);
        Assert.Equal(IslandStatus.Idle, state.Status);
        Assert.False(state.TurnRunning);
    }

    [Fact]
    public void WaitingApprovalFallsBackToIdle()
    {
        var state = new AgentSessionState
        {
            LastActivity = new DateTime(2026, 6, 22, 12, 0, 0),
            Status = IslandStatus.WaitingApproval,
            StatusText = "等待批准",
            TurnRunning = true
        };

        var result = AgentSessionStateMachine.Prune(
            state,
            state.LastActivity + AgentSessionStateMachine.WaitingApprovalEvict + TimeSpan.FromSeconds(1));

        Assert.True(result.Changed);
        Assert.False(result.Remove);
        Assert.Equal(IslandStatus.Idle, state.Status);
        Assert.False(state.TurnRunning);
    }

    [Fact]
    public void DoneFallsBackThenIdleSessionIsRemoved()
    {
        var state = new AgentSessionState
        {
            LastActivity = new DateTime(2026, 6, 22, 12, 0, 0),
            Status = IslandStatus.Done,
            StatusText = "完成"
        };

        var doneResult = AgentSessionStateMachine.Prune(
            state,
            state.LastActivity + AgentSessionStateMachine.DoneLinger + TimeSpan.FromSeconds(1));
        Assert.True(doneResult.Changed);
        Assert.False(doneResult.Remove);
        Assert.Equal(IslandStatus.Idle, state.Status);

        var idleResult = AgentSessionStateMachine.Prune(
            state,
            state.LastActivity + AgentSessionStateMachine.IdleEvict + TimeSpan.FromSeconds(1));
        Assert.False(idleResult.Changed);
        Assert.True(idleResult.Remove);
    }

    private static IngestEvent Event(string name, string? tool = null, string? message = null) =>
        new(
            Source: "claude",
            EventName: name,
            Tool: tool,
            SessionId: "s",
            Cwd: null,
            Effort: null,
            TranscriptPath: null,
            CurrentTask: null,
            Todos: null,
            Message: message);
}
