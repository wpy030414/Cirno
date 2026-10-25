using Cirno.Agents.Abstractions;
using Cirno.Core.Models;
using Cirno.Core.Persistence;

namespace Cirno.Core.Orchestration;

/// <summary>
/// 会话编排：创建/恢复会话、把事件转给订阅方、落库、限制并发、释放进程。
/// 不认识任何后端专有字段——差异都在适配器里消化完了。
/// </summary>
public sealed class SessionOrchestrator : IAsyncDisposable
{
    /// <summary>同时运行的后端会话上限；其余排队并显示「等待中」。</summary>
    public const int MaxConcurrentSessions = 4;

    private readonly SessionStore _store;
    private readonly Dictionary<string, IAgentBackend> _backends;
    private readonly Dictionary<string, IAgentHistorySource> _historySources;
    private readonly Action<int>? _onProcessStarted;
    private readonly Dictionary<string, Runtime> _runtimes = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _slots = new(MaxConcurrentSessions, MaxConcurrentSessions);
    private readonly object _gate = new();
    private bool _disposed;

    public SessionOrchestrator(
        SessionStore store,
        IEnumerable<IAgentBackend> backends,
        Action<int>? onProcessStarted = null)
    {
        _store = store;
        _backends = backends.ToDictionary(b => b.Id, StringComparer.OrdinalIgnoreCase);
        _historySources = [];
        foreach (var backend in _backends.Values)
        {
            if (backend.History is { } history)
            {
                _historySources[backend.Id] = history;
            }
        }

        _onProcessStarted = onProcessStarted;
    }

    /// <summary>归一化事件（适配器线程）。订阅方须自行切回 UI 线程。</summary>
    public event Action<string, AgentEvent>? SessionEvent;

    /// <summary>会话元数据变化（标题、模型、后端 id），用于刷新侧边栏。</summary>
    public event Action<string>? SessionChanged;

    /// <summary>编排层状态（排队、失败等），与适配器状态互补。</summary>
    public event Action<string, AgentSessionState>? SessionStateChanged;

    public IReadOnlyList<IAgentBackend> Backends => [.. _backends.Values];

    public IAgentBackend? GetBackend(string providerId)
        => _backends.TryGetValue(providerId, out var backend) ? backend : null;

    /// <summary>
    /// 会话列表：以各后端本地历史目录为权威源（ADR-132）。
    /// 目录里有什么会话就列什么；本地库只补充运行期字段（model/title）与
    /// 尚未落到历史目录的 pending 会话（backend_session_id 为空）。
    /// 扫描在后台线程做元数据级读取，不解析全文。
    /// </summary>
    public async Task<IReadOnlyList<SessionRecord>> GetSessionsAsync(CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            var merged = new Dictionary<string, SessionRecord>(StringComparer.Ordinal);

            foreach (var source in _historySources.Values)
            {
                foreach (var summary in source.ListSessions())
                {
                    var record = _store.GetSession(summary.SessionId);
                    merged[summary.SessionId] = new SessionRecord
                    {
                        Id = summary.SessionId,
                        ProviderId = source.ProviderId,
                        Model = record?.Model,
                        WorkingDirectory = summary.WorkingDirectory,
                        Title = summary.Title ?? source.NormalizeStoredTitle(record?.Title),
                        BackendSessionId = summary.SessionId,
                        CreatedAt = summary.FirstTimestamp ?? record?.CreatedAt ?? summary.LastActiveAt,
                        UpdatedAt = Max(summary.LastActiveAt, record?.UpdatedAt ?? DateTimeOffset.MinValue),
                    };
                }
            }

            // pending 会话：新建未发送、或启动失败但已有本地条目——历史目录里还没有它们的文件
            foreach (var record in _store.ListSessions())
            {
                if (!string.IsNullOrEmpty(record.BackendSessionId))
                {
                    continue;
                }

                // 草稿语义（ADR-133）：零条目的 pending 只是空壳（旧版本点「+」的残留），不列出。
                // 用户还没发出任何消息的会话不该占用侧栏。
                if (_store.CountItems(record.Id) == 0)
                {
                    continue;
                }

                merged.TryAdd(record.Id, record);
            }

            return (IReadOnlyList<SessionRecord>)[.. merged.Values.OrderByDescending(r => r.UpdatedAt)];
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 惰性加载会话条目；本地库缺条目、或历史文件比库新（会话在别处继续过）时，
    /// 先从历史目录导入再读库（ADR-132）。
    /// 活会话（进程在跑）直接读库——live 事件已实时落盘，重导入反而会跟写入竞态。
    /// </summary>
    public async Task<IReadOnlyList<AgentItem>> LoadItemsAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var runtime = GetRuntimeOrNull(sessionId);
        if (runtime?.Session is not null)
        {
            return _store.LoadItems(sessionId);
        }

        var record = _store.GetSession(sessionId);
        var needsImport = record is null || _store.CountItems(sessionId) == 0;

        var found = FindHistory(sessionId);
        if (found is ({ } source, { } summary))
        {
            if (!needsImport && summary.LastActiveAt > record!.UpdatedAt.ToUniversalTime())
            {
                needsImport = true;
            }

            if (needsImport)
            {
                var transcript = await Task.Run(
                    () => source.ReadTranscript(summary), cancellationToken).ConfigureAwait(false);
                if (transcript is not null)
                {
                    var imported = new SessionRecord
                    {
                        Id = sessionId,
                        ProviderId = source.ProviderId,
                        Model = record?.Model,
                        WorkingDirectory = summary.WorkingDirectory,
                        // 全量解析可能补到 64KB 窗口外的标题；旧库值再兜一层清洗
                        Title = transcript.Summary.Title
                                ?? summary.Title
                                ?? source.NormalizeStoredTitle(record?.Title),
                        BackendSessionId = summary.SessionId,
                        CreatedAt = summary.FirstTimestamp ?? record?.CreatedAt ?? summary.LastActiveAt,
                        UpdatedAt = summary.LastActiveAt,
                    };

                    _store.UpsertSession(imported);
                    _store.ReplaceItems(sessionId, transcript.Items);

                    // 已有运行时（从列表点开的）对齐新记录；没有也不强建——首次发送时 GetRuntime 自然补
                    runtime ??= GetRuntimeOrNull(sessionId);
                    if (runtime is not null)
                    {
                        lock (_gate)
                        {
                            runtime.Record = imported;
                        }
                    }

                    // 导入顺带修正了标题/时间戳：让侧栏立刻刷新，不必等重启
                    SessionChanged?.Invoke(sessionId);
                }
                // transcript 为 null：文件在列表面板与点开之间被外部删了，保持空时间线
            }
        }

        return _store.LoadItems(sessionId);
    }

    /// <summary>在历史目录里找会话的摘要；找不到返回 null（pending 会话或已外部删除）。</summary>
    private (IAgentHistorySource Source, HistoricalSessionSummary Summary)? FindHistory(string sessionId)
    {
        foreach (var source in _historySources.Values)
        {
            var summary = source.ListSessions().FirstOrDefault(s => s.SessionId == sessionId);
            if (summary is not null)
            {
                return (source, summary);
            }
        }

        return null;
    }

    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right) => left > right ? left : right;

    /// <summary>
    /// 直接往本地库写条目，不经过后端。
    /// 用于导入/迁移，以及开发期的性能自检（造一个万条消息的会话）。
    /// </summary>
    public IReadOnlyList<long> AppendItems(string sessionId, IEnumerable<AgentItem> items)
        => _store.AppendItems(sessionId, items);

    public SessionRecord CreateSession(string providerId, string workingDirectory, string? model = null)
    {
        if (!_backends.ContainsKey(providerId))
        {
            throw new InvalidOperationException($"未知的后端：{providerId}");
        }

        var record = new SessionRecord
        {
            // 必须是标准 UUID（带连字符）：Claude 的 --session-id 会校验格式，
            // 无连字符的 32 位十六进制会被后端直接拒绝。
            Id = Guid.NewGuid().ToString(),
            ProviderId = providerId,
            Model = model,
            WorkingDirectory = workingDirectory,
            Title = null,
            CreatedAt = DateTimeOffset.Now,
            UpdatedAt = DateTimeOffset.Now,
        };

        _store.UpsertSession(record);
        lock (_gate)
        {
            _runtimes[record.Id] = new Runtime { Record = record };
        }

        SessionChanged?.Invoke(record.Id);
        return record;
    }

    /// <summary>确保后端会话已启动。重启后首次发送会走 provider 自带的恢复路径。</summary>
    public async Task EnsureStartedAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var runtime = GetRuntime(sessionId);
        if (runtime.Session is not null)
        {
            return;
        }

        var backend = GetBackend(runtime.Record.ProviderId)
            ?? throw new InvalidOperationException($"未注册的后端：{runtime.Record.ProviderId}");

        var session = backend.CreateSession(new AgentSessionOptions
        {
            SessionId = runtime.Record.Id,
            WorkingDirectory = runtime.Record.WorkingDirectory,
            Model = runtime.Record.Model,
            ResumeSessionId = runtime.Record.BackendSessionId,
            OnProcessStarted = _onProcessStarted,
        });

        session.EventReceived += evt => OnSessionEvent(sessionId, evt);
        runtime.Session = session;

        await session.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SendAsync(string sessionId, string text, CancellationToken cancellationToken = default)
    {
        var runtime = GetRuntime(sessionId);

        // 同一会话的发送串行化，避免用户连击导致两条 user 消息交错写入
        await runtime.SendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureStartedAsync(sessionId, cancellationToken).ConfigureAwait(false);

            if (!runtime.SlotHeld)
            {
                if (_slots.CurrentCount == 0)
                {
                    SessionStateChanged?.Invoke(sessionId, AgentSessionState.Queued);
                }

                await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
                runtime.SlotHeld = true;
            }

            await runtime.Session!.SendUserMessageAsync(text, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            ReleaseSlot(runtime);
            throw;
        }
        finally
        {
            runtime.SendGate.Release();
        }
    }

    public Task InterruptAsync(string sessionId)
    {
        var runtime = GetRuntime(sessionId);
        return runtime.Session?.InterruptAsync() ?? Task.CompletedTask;
    }

    public Task ResolveApprovalAsync(string sessionId, string requestId, ApprovalOutcome outcome, string? message = null)
    {
        var runtime = GetRuntime(sessionId);
        return runtime.Session?.ResolveApprovalAsync(requestId, outcome, message) ?? Task.CompletedTask;
    }

    /// <summary>
    /// 删除会话：收尾进程 → 把历史转录移入回收站 → 清本地库。
    /// 历史目录是权威源（ADR-132），回收站失败就整个中止——库未动，可重试。
    /// </summary>
    public async Task DeleteAsync(string sessionId)
    {
        Runtime? runtime;
        lock (_gate)
        {
            if (!_runtimes.Remove(sessionId, out runtime))
            {
                runtime = null;
            }
        }

        if (runtime is not null)
        {
            if (runtime.Session is { } session)
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }

            ReleaseSlot(runtime);
        }

        foreach (var source in _historySources.Values)
        {
            if (source.TryDeleteTranscript(sessionId) == HistoryDeleteResult.Failed)
            {
                throw new InvalidOperationException("历史文件移入回收站失败，会话未被删除。");
            }
        }

        _store.DeleteSession(sessionId);
        SessionChanged?.Invoke(sessionId);
    }

    private void OnSessionEvent(string sessionId, AgentEvent evt)
    {
        switch (evt)
        {
            case SessionInitEvent init:
                UpdateRecord(sessionId, record => record with
                {
                    BackendSessionId = string.IsNullOrWhiteSpace(init.Info.BackendSessionId)
                        ? record.BackendSessionId
                        : init.Info.BackendSessionId,
                    Model = record.Model ?? init.Info.Model,
                    UpdatedAt = DateTimeOffset.Now,
                });
                break;

            case ItemCompletedEvent completed:
                _store.AppendItem(sessionId, completed.Item);
                if (completed.Item.Kind == AgentItemKind.UserMessage)
                {
                    TrySetTitle(sessionId, completed.Item.Text);
                }

                break;

            case TurnCompletedEvent:
                _store.TouchSession(sessionId);
                ReleaseSlot(GetRuntimeOrNull(sessionId));
                break;

            case AgentFaultEvent:
                ReleaseSlot(GetRuntimeOrNull(sessionId));
                break;
        }

        SessionEvent?.Invoke(sessionId, evt);
    }

    private void TrySetTitle(string sessionId, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var runtime = GetRuntimeOrNull(sessionId);
        if (runtime is null || !string.IsNullOrWhiteSpace(runtime.Record.Title))
        {
            return;
        }

        var title = string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (title.Length == 0)
        {
            return;
        }

        if (title.Length > 42)
        {
            title = title[..42] + "…";
        }

        UpdateRecord(sessionId, record => record with { Title = title, UpdatedAt = DateTimeOffset.Now });
    }

    private void UpdateRecord(string sessionId, Func<SessionRecord, SessionRecord> update)
    {
        Runtime? runtime;
        lock (_gate)
        {
            if (!_runtimes.TryGetValue(sessionId, out runtime))
            {
                return;
            }

            runtime.Record = update(runtime.Record);
        }

        _store.UpsertSession(runtime.Record);
        SessionChanged?.Invoke(sessionId);
    }

    private Runtime GetRuntime(string sessionId)
    {
        lock (_gate)
        {
            if (_runtimes.TryGetValue(sessionId, out var runtime))
            {
                return runtime;
            }
        }

        // 重启后首次访问：从库里补一个运行时
        var record = _store.GetSession(sessionId)
            ?? throw new InvalidOperationException($"会话不存在：{sessionId}");

        lock (_gate)
        {
            if (!_runtimes.TryGetValue(sessionId, out var created))
            {
                created = new Runtime { Record = record };
                _runtimes[sessionId] = created;
            }

            return created;
        }
    }

    private Runtime? GetRuntimeOrNull(string sessionId)
    {
        lock (_gate)
        {
            return _runtimes.TryGetValue(sessionId, out var runtime) ? runtime : null;
        }
    }

    private void ReleaseSlot(Runtime? runtime)
    {
        if (runtime is null)
        {
            return;
        }

        lock (_gate)
        {
            if (!runtime.SlotHeld)
            {
                return;
            }

            runtime.SlotHeld = false;
        }

        _slots.Release();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        List<IAgentSession> sessions;
        lock (_gate)
        {
            sessions = [.. _runtimes.Values.Select(r => r.Session).OfType<IAgentSession>()];
        }

        foreach (var session in sessions)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }

        _slots.Dispose();

        // 退出前把 WAL 落回主库：下次打开不必回放大量日志
        _store.Checkpoint();
    }

    private sealed class Runtime
    {
        public required SessionRecord Record { get; set; }

        public IAgentSession? Session { get; set; }

        public bool SlotHeld { get; set; }

        public SemaphoreSlim SendGate { get; } = new(1, 1);
    }
}
