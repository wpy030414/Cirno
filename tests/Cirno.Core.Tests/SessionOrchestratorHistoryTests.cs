using Cirno.Agents.Abstractions;
using Cirno.Core.Models;
using Cirno.Core.Orchestration;
using Cirno.Core.Persistence;
using Xunit;

namespace Cirno.Core.Tests;

/// <summary>
/// 编排层的历史目录权威源行为（ADR-132）：
/// 列表以目录扫描为准、打开未导入会话时惰性导入、文件更新后重导入、删除走回收站。
/// 后端与历史源都是假实现，不碰真实 ~/.claude。
/// </summary>
public sealed class SessionOrchestratorHistoryTests : IDisposable
{
    private readonly string _databasePath;
    private readonly FakeHistorySource _history = new();
    private readonly SessionStore _store;
    private readonly SessionOrchestrator _orchestrator;

    public SessionOrchestratorHistoryTests()
    {
        _databasePath = Path.Combine(
            Path.GetTempPath(),
            "cirno-core-tests",
            $"{Guid.NewGuid():n}.db");

        _store = new SessionStore(_databasePath);
        _orchestrator = new SessionOrchestrator(_store, [new FakeBackend(_history)]);
    }

    public void Dispose()
    {
        _orchestrator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _store.Dispose();

        try
        {
            if (File.Exists(_databasePath))
            {
                File.Delete(_databasePath);
            }
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task 列表权威_目录会话按最后活动排序()
    {
        const string olderId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
        const string newerId = "11111111-2222-3333-4444-555555555555";
        AddTranscript(olderId, "旧会话", lastActive: DateTimeOffset.UtcNow.AddHours(-3));
        AddTranscript(newerId, "新会话", lastActive: DateTimeOffset.UtcNow);

        var sessions = await _orchestrator.GetSessionsAsync();

        Assert.Equal([newerId, olderId], sessions.Select(s => s.Id).ToArray());
        Assert.All(sessions, s => Assert.Equal(s.Id, s.BackendSessionId));
    }

    [Fact]
    public async Task 库里有目录里没有_不列出_pending会话保留()
    {
        // 已落到历史目录又被人删了文件的会话：条目留库，但列表不再出现
        var orphan = new SessionRecord
        {
            Id = Guid.NewGuid().ToString(),
            ProviderId = "fake",
            WorkingDirectory = @"C:\gone",
            Title = "目录里已没有文件",
            BackendSessionId = "99999999-9999-9999-9999-999999999999",
            CreatedAt = DateTimeOffset.Now,
            UpdatedAt = DateTimeOffset.Now,
        };
        _store.UpsertSession(orphan);

        // pending：新建未发送，还没有转录文件；已有条目才会保留进列表
        var pending = _orchestrator.CreateSession("fake", @"C:\pending");
        _orchestrator.AppendItems(pending.Id,
        [
            new AgentItem { Id = "p-1", Kind = AgentItemKind.UserMessage, Text = "还没写进转录" },
        ]);

        var sessions = await _orchestrator.GetSessionsAsync();

        Assert.DoesNotContain(sessions, s => s.Id == orphan.Id);
        Assert.Contains(sessions, s => s.Id == pending.Id);
    }

    [Fact]
    public async Task 零条目的pending会话_不列出()
    {
        // 旧版本点「+」留下的空壳：没有条目、也没有转录文件，不该占侧栏
        var empty = _orchestrator.CreateSession("fake", @"C:\empty");

        var sessions = await _orchestrator.GetSessionsAsync();

        Assert.DoesNotContain(sessions, s => s.Id == empty.Id);
    }

    [Fact]
    public async Task 库里的命令回显标题_列表回退时被清洗()
    {
        var id = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
        AddTranscript(id, title: null, lastActive: DateTimeOffset.UtcNow);

        _store.UpsertSession(new SessionRecord
        {
            Id = id,
            ProviderId = "fake",
            WorkingDirectory = @"C:\external",
            Title = "<command-name>/clear</command-name>",
            BackendSessionId = id,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        });

        var session = Assert.Single(await _orchestrator.GetSessionsAsync());

        // 目录摘要没有标题 → 回退库里的旧值；旧值由历史源清洗（假源把 <command- 开头视为机器块）
        Assert.Null(session.Title);
    }

    [Fact]
    public async Task 打开未导入会话_导入且后端id与目录回填()
    {
        var id = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
        AddTranscript(id, "外部会话", lastActive: DateTimeOffset.UtcNow.AddMinutes(-5));

        var items = await _orchestrator.LoadItemsAsync(id);

        Assert.Equal(3, items.Count);
        Assert.Equal(AgentItemKind.UserMessage, items[0].Kind);
        Assert.Equal("外部会话的问题", items[0].Text);

        var record = _store.GetSession(id);
        Assert.NotNull(record);
        Assert.Equal(id, record!.BackendSessionId); // 续聊会走 --resume
        Assert.Equal(@"C:\external", record.WorkingDirectory);
        Assert.Equal("外部会话", record.Title);
    }

    [Fact]
    public async Task 文件比库新_重导入且不产生重复行()
    {
        var id = "11111111-2222-3333-4444-555555555555";
        var transcript = AddTranscript(id, "外部会话", lastActive: DateTimeOffset.UtcNow.AddHours(-2));

        await _orchestrator.LoadItemsAsync(id);
        Assert.Equal(3, _store.LoadItems(id).Count);

        // 会话在别处继续过：文件 mtime 前移，条目从 3 变 5
        transcript.Summary = transcript.Summary with
        {
            LastActiveAt = DateTimeOffset.UtcNow,
        };
        transcript.Items =
        [
            transcript.Items[0],
            transcript.Items[1],
            transcript.Items[2],
            new AgentItem { Id = "new-1", Kind = AgentItemKind.AssistantMessage, Text = "续聊回答" },
            new AgentItem { Id = "new-2", Kind = AgentItemKind.AssistantMessage, Text = "补充" },
        ];

        await _orchestrator.LoadItemsAsync(id);

        var reloaded = _store.LoadItems(id);
        Assert.Equal(5, reloaded.Count); // 整体重建（ReplaceItems），不是叠加
    }

    [Fact]
    public async Task 活会话_不重导入()
    {
        var id = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
        AddTranscript(id, "运行中", lastActive: DateTimeOffset.UtcNow);

        await _orchestrator.LoadItemsAsync(id);
        Assert.Equal(1, _history.ReadCount);

        // 拉起假会话：等价于「进程在跑，live 事件正在实时落库」
        await _orchestrator.EnsureStartedAsync(id);

        var items = await _orchestrator.LoadItemsAsync(id);

        // 仍然读得到已导入的 3 条，但 ReadTranscript 没有再被碰——重导入会跟 live 写入竞态
        Assert.Equal(3, items.Count);
        Assert.Equal(1, _history.ReadCount);
    }

    [Fact]
    public async Task 删除_转录进回收站且本地库清空()
    {
        var id = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
        AddTranscript(id, "待删除", lastActive: DateTimeOffset.UtcNow);
        await _orchestrator.LoadItemsAsync(id);

        await _orchestrator.DeleteAsync(id);

        Assert.Equal("C:\\fake\\" + id + ".jsonl", _history.DeletedPath);
        Assert.True(_history.DeleterResult); // deleter 被调用且成功
        Assert.Null(_store.GetSession(id));
        Assert.Equal(0, _store.CountItems(id));
        Assert.Empty(await _orchestrator.GetSessionsAsync());
    }

    [Fact]
    public async Task 删除_回收站失败_抛异常且库保留()
    {
        var id = "11111111-2222-3333-4444-555555555555";
        AddTranscript(id, "删不掉", lastActive: DateTimeOffset.UtcNow);
        await _orchestrator.LoadItemsAsync(id);
        _history.DeleterResult = false;

        await Assert.ThrowsAsync<InvalidOperationException>(() => _orchestrator.DeleteAsync(id));

        Assert.NotNull(_store.GetSession(id)); // 库未动，可重试
        Assert.Equal(3, _store.CountItems(id));
    }

    [Fact]
    public async Task 删除_pending会话_目录NotFound也照样清库()
    {
        var pending = _orchestrator.CreateSession("fake", @"C:\pending");

        await _orchestrator.DeleteAsync(pending.Id);

        Assert.Null(_store.GetSession(pending.Id));
    }

    // ---------- 夹具 ----------

    private FakeTranscript AddTranscript(string sessionId, string? title, DateTimeOffset lastActive)
    {
        var transcript = new FakeTranscript
        {
            Summary = new HistoricalSessionSummary
            {
                SessionId = sessionId,
                WorkingDirectory = @"C:\external",
                FirstTimestamp = lastActive.AddHours(-1),
                LastActiveAt = lastActive,
                Title = title,
                FilePath = @"C:\fake\" + sessionId + ".jsonl",
            },
            Items =
            [
                new AgentItem { Id = "u-1", Kind = AgentItemKind.UserMessage, Text = "外部会话的问题" },
                new AgentItem { Id = "a-1", Kind = AgentItemKind.AssistantMessage, Text = "外部会话的回答" },
                new AgentItem { Id = "t-1", Kind = AgentItemKind.ToolCall, ToolName = "Bash" },
            ],
        };

        _history.Transcripts[sessionId] = transcript;
        return transcript;
    }

    private sealed class FakeBackend(IAgentHistorySource history) : IAgentBackend
    {
        public string Id => "fake";

        public string DisplayName => "Fake";

        public IAgentHistorySource? History => history;

        public Task<string?> ProbeAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

        public IAgentSession CreateSession(AgentSessionOptions options) => new FakeAgentSession();
    }

    private sealed class FakeAgentSession : IAgentSession
    {
        public string SessionId => "fake";

        public AgentSessionState State => AgentSessionState.Idle;

        public string WorkingDirectory => @"C:\external";

        public event Action<AgentEvent>? EventReceived
        {
            add { }
            remove { }
        }

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SendUserMessageAsync(string text, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ResolveApprovalAsync(string requestId, ApprovalOutcome outcome, string? message = null) => Task.CompletedTask;

        public Task InterruptAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>假历史源：内存字典 + 可控的删除结果。</summary>
    private sealed class FakeHistorySource : IAgentHistorySource
    {
        public Dictionary<string, FakeTranscript> Transcripts { get; } = new(StringComparer.Ordinal);

        public string? DeletedPath { get; private set; }

        public bool DeleterResult { get; set; } = true;

        public string ProviderId => "fake";

        public IReadOnlyList<HistoricalSessionSummary> ListSessions()
            => [.. Transcripts.Values.Select(t => t.Summary).OrderByDescending(s => s.LastActiveAt)];

        public HistoricalTranscript? ReadTranscript(HistoricalSessionSummary summary)
        {
            ReadCount++;
            return Transcripts.TryGetValue(summary.SessionId, out var transcript)
                ? new HistoricalTranscript { Summary = summary, Items = transcript.Items }
                : null;
        }

        public int ReadCount { get; private set; }

        /// <summary>模拟 Claude 适配器的旧标题清洗：机器块标题一律丢弃。</summary>
        public string? NormalizeStoredTitle(string? title)
            => title is not null && title.StartsWith("<command-", StringComparison.Ordinal) ? null : title;

        public HistoryDeleteResult TryDeleteTranscript(string sessionId)
        {
            if (!Transcripts.TryGetValue(sessionId, out var transcript))
            {
                return HistoryDeleteResult.NotFound;
            }

            DeletedPath = transcript.Summary.FilePath;
            if (!DeleterResult)
            {
                return HistoryDeleteResult.Failed;
            }

            // 真实世界：文件进了回收站，之后的扫描自然看不到它
            Transcripts.Remove(sessionId);
            return HistoryDeleteResult.Deleted;
        }
    }

    private sealed class FakeTranscript
    {
        public required HistoricalSessionSummary Summary { get; set; }

        public required IReadOnlyList<AgentItem> Items { get; set; }
    }
}
