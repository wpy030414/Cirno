namespace Cirno.Agents.Abstractions;

/// <summary>适配器产出的归一化事件。界面与 Core 只消费这些类型。</summary>
public abstract record AgentEvent
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
}

/// <summary>后端会话就绪：会话 id、实际生效模型、可用工具、权限模式。</summary>
public sealed record SessionInitEvent(SessionInit Info) : AgentEvent;

/// <summary>一轮工作开始。</summary>
public sealed record TurnStartedEvent(string TurnId, string? Model) : AgentEvent;

/// <summary>条目开始（流式内容的第一帧或完整块到达）。</summary>
public sealed record ItemStartedEvent(AgentItem Item) : AgentEvent;

/// <summary>条目增量。仅用于流式文本；权威内容以 <see cref="ItemCompletedEvent"/> 为准。</summary>
public sealed record ItemDeltaEvent(string ItemId, string Delta) : AgentEvent;

/// <summary>条目完成，此时的内容为权威态。</summary>
public sealed record ItemCompletedEvent(AgentItem Item) : AgentEvent;

/// <summary>后端请求审批。必须回应，否则后端工具调用会永久阻塞。</summary>
public sealed record ApprovalRequestedEvent(ApprovalRequest Request) : AgentEvent;

/// <summary>审批已有终态（用户决策或会话销毁时的兜底拒绝）。</summary>
public sealed record ApprovalResolvedEvent(string RequestId, ApprovalOutcome Outcome) : AgentEvent;

/// <summary>一轮工作结束，含状态与用量。</summary>
public sealed record TurnCompletedEvent(TurnResult Result) : AgentEvent;

/// <summary>可归一化的错误。</summary>
public sealed record AgentFaultEvent(AgentError Error) : AgentEvent;

/// <summary>后端状态变化（进程存活、重连、压缩上下文等），不打断渲染。</summary>
public sealed record SessionStateChangedEvent(AgentSessionState State) : AgentEvent;
