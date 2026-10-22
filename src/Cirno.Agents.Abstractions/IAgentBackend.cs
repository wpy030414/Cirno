namespace Cirno.Agents.Abstractions;

/// <summary>一个后端会话的生命周期。实现必须保证进程树在释放时被清理。</summary>
public interface IAgentSession : IAsyncDisposable
{
    string SessionId { get; }

    AgentSessionState State { get; }

    /// <summary>会话私有工作目录，后端据此定位历史与工程语义。</summary>
    string WorkingDirectory { get; }

    /// <summary>归一化事件流。订阅方须自行切回 UI 线程。</summary>
    event Action<AgentEvent>? EventReceived;

    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>发送一条用户消息（纯文本）。</summary>
    Task SendUserMessageAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>应答一次审批。必须是终态，且每个请求只应答一次。</summary>
    Task ResolveApprovalAsync(string requestId, ApprovalOutcome outcome, string? message = null);

    /// <summary>中断当前轮次。</summary>
    Task InterruptAsync(CancellationToken cancellationToken = default);
}

/// <summary>后端工厂：负责把会话参数翻译成一次进程/连接启动。</summary>
public interface IAgentBackend
{
    /// <summary>稳定标识，用作进程池键与配置键。</summary>
    string Id { get; }

    string DisplayName { get; }

    /// <summary>探测后端可用性；不可用时返回可读原因。</summary>
    Task<string?> ProbeAsync(CancellationToken cancellationToken = default);

    IAgentSession CreateSession(AgentSessionOptions options);
}

public sealed record AgentSessionOptions
{
    /// <summary>会话标识由宿主生成，使本地记录与后端会话一一对应。</summary>
    public required string SessionId { get; init; }
    public required string WorkingDirectory { get; init; }
    public string? Model { get; init; }
    public string? PermissionMode { get; init; }
    /// <summary>恢复已有后端会话时使用。</summary>
    public string? ResumeSessionId { get; init; }

    /// <summary>
    /// 子进程启动后的回调（参数为 PID）。宿主用它把进程加入 Job Object，
    /// 保证 Cirno 异常退出时不留下孤儿进程。适配器不关心回调做了什么。
    /// </summary>
    public Action<int>? OnProcessStarted { get; init; }
}
