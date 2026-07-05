using DynamicIsland.Core.Ingest;
using DynamicIsland.Core.Model;

namespace DynamicIsland.Core.State;

public sealed class AgentSessionState
{
    public DateTime LastActivity { get; set; }
    public string Tool { get; set; } = "";
    public IslandStatus Status { get; set; } = IslandStatus.Idle;
    public string StatusText { get; set; } = "";
    public DateTime TurnStart { get; set; }
    public TimeSpan LastTurnElapsed { get; set; } = TimeSpan.Zero;
    public bool TurnRunning { get; set; }
}

public readonly record struct SessionEventResult(bool ArmToolHold, bool CancelToolHold);

public readonly record struct PruneResult(bool Changed, bool Remove);

public static class AgentSessionStateMachine
{
    public static readonly TimeSpan DoneLinger = TimeSpan.FromSeconds(6);
    public static readonly TimeSpan IdleEvict = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan WaitingApprovalEvict = TimeSpan.FromSeconds(180);
    public static readonly TimeSpan ActiveSilenceEvict = TimeSpan.FromMinutes(10);

    public static SessionEventResult ApplyEvent(AgentSessionState state, IngestEvent evt, DateTime now)
    {
        state.LastActivity = now;

        switch (evt.EventName)
        {
            case "UserPromptSubmit":
                state.TurnStart = now;
                state.TurnRunning = true;
                state.Tool = "";
                ApplyStatus(state, IslandStatus.Thinking, "思考中…");
                return new SessionEventResult(ArmToolHold: false, CancelToolHold: true);

            case "PreToolUse":
                if (string.Equals(evt.Tool, "TodoWrite", StringComparison.OrdinalIgnoreCase))
                {
                    ApplyStatus(state, IslandStatus.Thinking, "思考中…");
                }
                else
                {
                    state.Tool = evt.Tool ?? "工具";
                    ApplyStatus(state, IslandStatus.RunningTool, state.Tool);
                }
                return new SessionEventResult(ArmToolHold: false, CancelToolHold: true);

            case "PostToolUse":
                return new SessionEventResult(ArmToolHold: state.Status == IslandStatus.RunningTool, CancelToolHold: false);

            case "Notification":
                if (IsApprovalRequest(evt.Message) && state.TurnRunning)
                {
                    ApplyStatus(state, IslandStatus.WaitingApproval, "等待批准");
                    return new SessionEventResult(ArmToolHold: false, CancelToolHold: true);
                }
                break;

            case "Stop":
                if (state.TurnRunning)
                {
                    state.LastTurnElapsed = now - state.TurnStart;
                    state.TurnRunning = false;
                }
                ApplyStatus(state, IslandStatus.Done, "完成");
                return new SessionEventResult(ArmToolHold: false, CancelToolHold: true);
        }

        return new SessionEventResult(ArmToolHold: false, CancelToolHold: false);
    }

    public static bool ApplyToolHoldElapsed(AgentSessionState state)
    {
        if (state.Status != IslandStatus.RunningTool) return false;
        ApplyStatus(state, IslandStatus.Thinking, "思考中…");
        return true;
    }

    public static PruneResult Prune(AgentSessionState state, DateTime now)
    {
        var elapsed = now - state.LastActivity;
        var wasIdle = state.Status == IslandStatus.Idle;
        var changed = false;

        if (state.Status == IslandStatus.Done && elapsed > DoneLinger)
        {
            ApplyStatus(state, IslandStatus.Idle, "");
            changed = true;
        }

        if (state.Status == IslandStatus.WaitingApproval && elapsed > WaitingApprovalEvict)
        {
            ApplyStatus(state, IslandStatus.Idle, "");
            state.TurnRunning = false;
            changed = true;
        }

        if ((state.Status == IslandStatus.Thinking || state.Status == IslandStatus.RunningTool)
            && elapsed > ActiveSilenceEvict)
        {
            ApplyStatus(state, IslandStatus.Idle, "");
            state.TurnRunning = false;
            changed = true;
        }

        var remove = wasIdle && elapsed > IdleEvict;
        return new PruneResult(changed, remove);
    }

    public static int Weight(IslandStatus status) => status switch
    {
        IslandStatus.WaitingApproval => 4,
        IslandStatus.RunningTool => 3,
        IslandStatus.Thinking => 2,
        IslandStatus.Done => 1,
        _ => 0
    };

    public static bool IsApprovalRequest(string? message)
    {
        if (string.IsNullOrEmpty(message)) return false;
        return message.Contains("permission", StringComparison.OrdinalIgnoreCase)
            || message.Contains("approve", StringComparison.OrdinalIgnoreCase)
            || message.Contains("approval", StringComparison.OrdinalIgnoreCase);
    }

    private static void ApplyStatus(AgentSessionState state, IslandStatus status, string text)
    {
        state.Status = status;
        state.StatusText = text;
    }
}
