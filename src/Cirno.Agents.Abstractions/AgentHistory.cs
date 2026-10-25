namespace Cirno.Agents.Abstractions;

/// <summary>
/// 后端本地历史会话目录的读取契约。
/// <para>
/// 会话列表以历史目录为权威源：目录里有什么会话，界面就显示什么会话。
/// Core 只消费本接口，不感知任何目录布局细节（那是适配器的事）。
/// </para>
/// </summary>
public interface IAgentHistorySource
{
    /// <summary>与 <see cref="IAgentBackend.Id"/> 一致，用于把历史会话归到对应后端。</summary>
    string ProviderId { get; }

    /// <summary>
    /// 元数据级扫描：列出历史目录中的全部会话。
    /// 实现不得做全量解析（ADR-104 的启动性能红线），每文件只允许窥读取摘要。
    /// </summary>
    IReadOnlyList<HistoricalSessionSummary> ListSessions();

    /// <summary>全量解析一个会话的转录，产出归一化条目（行序即时间线序）。</summary>
    HistoricalTranscript? ReadTranscript(HistoricalSessionSummary summary);

    /// <summary>把会话的转录文件移出历史目录（移入回收站）。删除会话时由编排层调用。</summary>
    HistoryDeleteResult TryDeleteTranscript(string sessionId);
}

/// <summary>删除转录的结果三态。</summary>
public enum HistoryDeleteResult
{
    /// <summary>历史目录里找不到该会话（可能已被外部删除）。</summary>
    NotFound,

    Deleted,

    /// <summary>删除失败（权限、占用等）。调用方应当保留本地记录并允许重试。</summary>
    Failed,
}

/// <summary>历史会话的元数据摘要。</summary>
public sealed record HistoricalSessionSummary
{
    /// <summary>后端会话 id。对 Claude 而言即转录文件名（标准 UUID）。</summary>
    public required string SessionId { get; init; }

    public required string WorkingDirectory { get; init; }

    /// <summary>首条带时间戳的行（UTC）；头部噪声行没有时间戳时可能为空。</summary>
    public DateTimeOffset? FirstTimestamp { get; init; }

    /// <summary>最后活动时间（文件写入时间）。</summary>
    public required DateTimeOffset LastActiveAt { get; init; }

    /// <summary>标题；来源由实现决定（Claude：ai-title &gt; summary &gt; 首条用户消息截断）。</summary>
    public string? Title { get; init; }

    /// <summary>转录源文件路径。Core 只透传，不解释。</summary>
    public string? FilePath { get; init; }
}

/// <summary>一次全量解析的结果。</summary>
public sealed record HistoricalTranscript
{
    public required HistoricalSessionSummary Summary { get; init; }

    /// <summary>归一化条目，按转录行序排列。</summary>
    public required IReadOnlyList<AgentItem> Items { get; init; }

    /// <summary>末条带时间戳的行；目录为空时为 null。</summary>
    public DateTimeOffset? LastTimestamp { get; init; }
}
