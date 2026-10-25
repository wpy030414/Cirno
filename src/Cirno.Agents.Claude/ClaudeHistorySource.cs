using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cirno.Agents.Abstractions;

namespace Cirno.Agents.Claude;

/// <summary>
/// Claude Code 本地历史目录（&lt;CLAUDE_CONFIG_DIR&gt;/projects，默认 ~/.claude/projects）读取器。
/// <para>
/// 目录布局（在真实 CLI 上实测，2.1.301）：
/// <c>projects/&lt;munged-cwd&gt;/&lt;session-uuid&gt;.jsonl</c>，一行一个事件；
/// munged-cwd 是把路径里的非字母数字字符替换成 '-' 的产物，不可逆。
/// 同目录还会有 memory/ 与 &lt;uuid&gt;/（子代理转录）子目录——只枚举一级 jsonl 文件即可全部跳过。
/// </para>
/// <para>
/// 会话列表只做元数据扫描（每文件窥读前 64KB，ADR-104 的启动性能红线）；
/// 时间线导入才走 <see cref="ReadTranscript"/> 全量解析。
/// </para>
/// </summary>
public sealed class ClaudeHistorySource : IAgentHistorySource
{
    /// <summary>摘要窥读的上限：足够覆盖头部噪声行 + 首条用户消息 + 标题行。</summary>
    private const int PeekBytes = 64 * 1024;

    /// <summary>标题截断长度，与编排层 TrySetTitle 的规则一致。</summary>
    private const int TitleMaxLength = 42;

    private readonly string _rootDirectory;
    private readonly Func<string, bool> _recycleDeleter;

    public ClaudeHistorySource(string? rootDirectory = null, Func<string, bool>? recycleDeleter = null)
    {
        _rootDirectory = rootDirectory ?? ResolveDefaultRoot();
        _recycleDeleter = recycleDeleter ?? RecycleBin.TrySend;
    }

    public string ProviderId => "claude";

    public IReadOnlyList<HistoricalSessionSummary> ListSessions()
    {
        var summaries = new List<HistoricalSessionSummary>();

        if (!Directory.Exists(_rootDirectory))
        {
            return summaries;
        }

        try
        {
            // 只看一级：projects/<munged>/<uuid>.jsonl。memory/、<uuid>/subagents/ 是目录，天然出局
            foreach (var projectDir in Directory.EnumerateDirectories(_rootDirectory))
            {
                foreach (var file in Directory.EnumerateFiles(projectDir, "*.jsonl"))
                {
                    if (TrySummarize(projectDir, file) is { } summary)
                    {
                        summaries.Add(summary);
                    }
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            // 目录级权限问题：拿到多少算多少
        }

        return summaries;
    }

    public HistoricalTranscript? ReadTranscript(HistoricalSessionSummary summary)
    {
        if (summary.FilePath is null || !File.Exists(summary.FilePath))
        {
            return null;
        }

        var collector = new TranscriptCollector();
        var parser = new ClaudeEventParser(collector) { EmitUserMessages = true };

        try
        {
            // FileShare.ReadWrite：转录可能正被 CLI 追加写入
            using var stream = new FileStream(
                summary.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);

            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    parser.HandleLine(line);
                }
                catch (JsonException)
                {
                    // 半行（并发写入窗口）或坏行：跳过，宽容解析
                }
            }
        }
        catch (IOException)
        {
            return null;
        }

        var items = collector.Items;

        // 64KB 窥读窗口之外才出现首条人类消息时（头部噪声很长），全量解析可以补上标题：
        // 解析结果里的 UserMessage 已经过机器块清洗，直接拿来当标题来源。
        var enriched = summary;
        if (string.IsNullOrWhiteSpace(summary.Title))
        {
            var firstHuman = items.FirstOrDefault(
                i => i.Kind == AgentItemKind.UserMessage && !string.IsNullOrWhiteSpace(i.Text));
            if (firstHuman is not null)
            {
                enriched = summary with { Title = NormalizeTitle(firstHuman.Text) };
            }
        }

        return new HistoricalTranscript
        {
            Summary = enriched,
            Items = items,
            LastTimestamp = items.Count > 0 ? items.Max(i => i.Timestamp) : null,
        };
    }

    public HistoryDeleteResult TryDeleteTranscript(string sessionId)
    {
        var path = FindTranscriptPath(sessionId);
        if (path is null)
        {
            return HistoryDeleteResult.NotFound;
        }

        try
        {
            return _recycleDeleter(path) ? HistoryDeleteResult.Deleted : HistoryDeleteResult.Failed;
        }
        catch
        {
            return HistoryDeleteResult.Failed;
        }
    }

    /// <summary>
    /// 旧版本曾把斜杠命令回显写进库里的标题；列表回退到库值时过一遍同一清洗器。
    /// </summary>
    public string? NormalizeStoredTitle(string? title) => ClaudeUserText.Sanitize(title);

    // ---------- 内部 ----------

    private string? FindTranscriptPath(string sessionId)
    {
        if (!Directory.Exists(_rootDirectory))
        {
            return null;
        }

        try
        {
            return Directory.EnumerateDirectories(_rootDirectory)
                .Select(dir => Path.Combine(dir, sessionId + ".jsonl"))
                .FirstOrDefault(File.Exists);
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string ResolveDefaultRoot()
    {
        // CLAUDE_CONFIG_DIR 可把整个配置根挪走；projects 是其下的固定布局
        var configRoot = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        if (string.IsNullOrWhiteSpace(configRoot))
        {
            configRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        }

        return Path.Combine(configRoot, "projects");
    }

    private static HistoricalSessionSummary? TrySummarize(string projectDir, string file)
    {
        var sessionId = Path.GetFileNameWithoutExtension(file);

        // 文件名即后端会话 id：必须是标准 UUID（带连字符）
        if (!Guid.TryParseExact(sessionId, "D", out _))
        {
            return null;
        }

        string? cwd = null;
        string? firstTimestampText = null;
        string? firstUserText = null;
        string? aiTitle = null;
        string? summaryTitle = null;

        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buffer = new byte[PeekBytes];
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read <= 0)
            {
                return null; // 空文件：CLI 尚未写入任何事件
            }

            var text = Encoding.UTF8.GetString(buffer, 0, read);

            // 截断时丢弃尾部半行；未截断的最后一行（无换行符）也要补进来
            var truncated = read == buffer.Length;
            var lastNewline = text.LastIndexOf('\n');
            if (truncated && lastNewline >= 0)
            {
                text = text[..(lastNewline + 1)];
            }

            foreach (var line in EnumerateLines(text))
            {
                JsonDocument doc;
                try
                {
                    doc = JsonDocument.Parse(line);
                }
                catch (JsonException)
                {
                    continue;
                }

                using (doc)
                {
                    var root = doc.RootElement;

                    cwd ??= ClaudeEventParser.GetString(root, "cwd");
                    firstTimestampText ??= ClaudeEventParser.GetString(root, "timestamp");

                    switch (ClaudeEventParser.GetString(root, "type"))
                    {
                        case "ai-title":
                            aiTitle = ClaudeEventParser.GetString(root, "aiTitle") ?? aiTitle;
                            break;

                        case "summary":
                            summaryTitle = ClaudeEventParser.GetString(root, "summary") ?? summaryTitle;
                            break;

                        case "user":
                            if (firstUserText is null && IsHumanUserLine(root))
                            {
                                firstUserText = ReadPromptText(root);
                            }

                            break;
                    }
                }
            }
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        DateTimeOffset? firstTimestamp = null;
        if (firstTimestampText is not null &&
            DateTimeOffset.TryParse(firstTimestampText,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed))
        {
            firstTimestamp = parsed;
        }

        return new HistoricalSessionSummary
        {
            SessionId = sessionId,
            // cwd 每条消息行都带；64KB 内拿不到（头部噪声占满，极罕见）时回退 munged 目录名。
            // munging 不可逆，回退值只够分组展示，不够拿来恢复进程。
            WorkingDirectory = cwd ?? new DirectoryInfo(projectDir).Name,
            FirstTimestamp = firstTimestamp,
            LastActiveAt = File.GetLastWriteTimeUtc(file),
            Title = NormalizeTitle(aiTitle ?? summaryTitle ?? firstUserText),
            FilePath = file,
        };
    }

    private static IEnumerable<string> EnumerateLines(string text)
    {
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                yield return text[start..i].TrimEnd('\r');
                start = i + 1;
            }
        }

        if (start < text.Length)
        {
            yield return text[start..].TrimEnd('\r');
        }
    }

    private static bool IsHumanUserLine(JsonElement root)
    {
        if (root.TryGetProperty("isMeta", out var meta) && meta.ValueKind == JsonValueKind.True)
        {
            return false;
        }

        if (root.TryGetProperty("isSidechain", out var sidechain) && sidechain.ValueKind == JsonValueKind.True)
        {
            return false;
        }

        if (root.TryGetProperty("origin", out var origin) &&
            ClaudeEventParser.GetString(origin, "kind") is { } kind && kind != "human")
        {
            return false;
        }

        return true;
    }

    private static string? ReadPromptText(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var message) ||
            !message.TryGetProperty("content", out var content))
        {
            return null;
        }

        var raw = content.ValueKind switch
        {
            JsonValueKind.String => content.GetString(),
            JsonValueKind.Array => string.Join('\n', content.EnumerateArray()
                .Where(b => ClaudeEventParser.GetString(b, "type") == "text")
                .Select(b => ClaudeEventParser.GetString(b, "text"))),
            _ => null,
        };

        // 斜杠命令回显、宿主提醒不是用户说的话；清洗后为空则不占位，
        // 让扫描继续找后面真正的人类消息。
        return ClaudeUserText.Sanitize(raw);
    }

    /// <summary>折行合并、截断，与编排层 TrySetTitle 同一条规则。</summary>
    private static string? NormalizeTitle(string? text)
    {
        var sanitized = ClaudeUserText.Sanitize(text);
        if (sanitized is null)
        {
            return null;
        }

        var title = string.Join(' ', sanitized.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (title.Length == 0)
        {
            return null;
        }

        return title.Length > TitleMaxLength ? title[..TitleMaxLength] + "…" : title;
    }

    /// <summary>转录解析的事件收集器：按到达序收 ItemCompleted，同 id 以后到者覆盖（保首现位）。</summary>
    private sealed class TranscriptCollector : IClaudeStreamSink
    {
        private readonly List<AgentItem> _items = [];
        private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);

        public IReadOnlyList<AgentItem> Items => _items;

        public void Raise(AgentEvent evt)
        {
            if (evt is not ItemCompletedEvent completed)
            {
                return;
            }

            var id = completed.Item.Id;
            if (_index.TryGetValue(id, out var position))
            {
                _items[position] = completed.Item;
            }
            else
            {
                _index[id] = _items.Count;
                _items.Add(completed.Item);
            }
        }

        public Task WriteControlAsync(JsonObject payload, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
