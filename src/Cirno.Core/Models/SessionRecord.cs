namespace Cirno.Core.Models;

/// <summary>会话元数据。provider 是进程级属性，因此建会话时确定、之后不可改。</summary>
public sealed record SessionRecord
{
    public required string Id { get; init; }
    public required string ProviderId { get; init; }
    public string? Model { get; init; }
    public required string WorkingDirectory { get; init; }
    public string? Title { get; init; }

    /// <summary>后端侧会话 id：Claude 由宿主指定，Codex 由后端返回；用于恢复。</summary>
    public string? BackendSessionId { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;

    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.Now;
}
