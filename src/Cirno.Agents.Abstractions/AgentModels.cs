using System.Text.Json;

namespace Cirno.Agents.Abstractions;

/// <summary>归一化条目类型。后端专有概念必须在适配器里映射到这些值之一。</summary>
public enum AgentItemKind
{
    UserMessage,
    AssistantMessage,
    Reasoning,
    Plan,
    CommandExecution,
    FileChange,
    ToolCall,
    Approval,
    Notice,
    Error,
    Unknown,
}

/// <summary>会话状态机。UI 只依赖这些状态，不依赖后端字段。</summary>
public enum AgentSessionState
{
    Starting,
    /// <summary>已排队：并发上限已满，等待空位。</summary>
    Queued,
    Idle,
    Running,
    AwaitingApproval,
    Interrupted,
    Failed,
}

/// <summary>
/// 归一化条目。<see cref="Raw"/> 保留后端原始载荷，供调试视图使用，
/// 但界面层不得读取其中的后端专有字段。
/// </summary>
public sealed record AgentItem
{
    public required string Id { get; init; }
    public required AgentItemKind Kind { get; init; }
    public string? Text { get; init; }
    public string? Title { get; init; }
    public string? Detail { get; init; }
    public string? ToolName { get; init; }
    public string? ToolUseId { get; init; }
    public bool IsError { get; init; }
    public bool IsStreaming { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
    public JsonElement? Raw { get; init; }
}

/// <summary>会话就绪后回填的后端事实。</summary>
public sealed record SessionInit
{
    public required string BackendSessionId { get; init; }
    public string? Model { get; init; }
    public string? PermissionMode { get; init; }
    public string? BackendVersion { get; init; }
    public IReadOnlyList<string> Tools { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>用户对一次审批的决策。后端独有扩展留在适配器内部处理。</summary>
public enum ApprovalOutcome
{
    Allow,
    Deny,
    Cancel,
}

public sealed record ApprovalRequest
{
    public required string RequestId { get; init; }
    public required string ToolName { get; init; }
    public string? ToolUseId { get; init; }
    public string? Title { get; init; }
    public string? Detail { get; init; }
    /// <summary>后端给出的决策依据（规则来源等），仅用于展示。</summary>
    public string? Reason { get; init; }
    public JsonElement? Input { get; init; }
}

/// <summary>一轮工作的结果与用量。</summary>
public sealed record TurnResult
{
    public bool IsError { get; init; }
    public string? StopReason { get; init; }
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public decimal CostUsd { get; init; }
    public TimeSpan Duration { get; init; }
}

/// <summary>归一化错误：后端错误码 + 可读文案 + 是否可重试。</summary>
public sealed record AgentError
{
    public required string Code { get; init; }
    public required string Message { get; init; }
    public bool IsRetryable { get; init; }
}
