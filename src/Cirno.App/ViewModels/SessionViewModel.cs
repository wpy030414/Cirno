using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using Cirno.Agents.Abstractions;
using Cirno.Agents.Claude;
using Microsoft.UI.Dispatching;

namespace Cirno.App.ViewModels;

/// <summary>
/// 单会话视图模型：持有一个后端会话，把归一化事件转成时间线条目。
/// 流式增量不逐条刷新界面，而是按固定帧率合帧提交。
/// </summary>
public sealed class SessionViewModel : ObservableObject, IAsyncDisposable
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(33);

    private readonly DispatcherQueue _dispatcher;
    private readonly ClaudeBackend _backend = new();
    private readonly Dictionary<string, TimelineItem> _index = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StringBuilder> _pendingDeltas = new(StringComparer.Ordinal);
    private readonly DispatcherQueueTimer _flushTimer;

    private IAgentSession? _session;
    private string _statusText = "未启动";
    private string _modelText = "—";
    private string _usageText = string.Empty;
    private bool _isBusy;
    private string _workingDirectory;

    public SessionViewModel(
        DispatcherQueue dispatcher,
        string workingDirectory,
        bool autoApprove = false,
        string? tracePath = null)
    {
        _dispatcher = dispatcher;
        _workingDirectory = workingDirectory;
        AutoApprove = autoApprove;
        TracePath = tracePath;

        _flushTimer = dispatcher.CreateTimer();
        _flushTimer.Interval = FlushInterval;
        _flushTimer.IsRepeating = true;
        _flushTimer.Tick += (_, _) => FlushDeltas();
    }

    /// <summary>开发期自检：自动放行审批，用于无人值守跑通链路。</summary>
    public bool AutoApprove { get; }

    /// <summary>开发期自检：把归一化事件落到 JSONL，便于断言。</summary>
    public string? TracePath { get; }

    public ObservableCollection<TimelineItem> Items { get; } = [];

    /// <summary>内容变化需要滚动到底部时触发（UI 线程）。</summary>
    public event EventHandler? ScrollRequested;

    public string WorkingDirectory
    {
        get => _workingDirectory;
        private set => Set(ref _workingDirectory, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public string ModelText
    {
        get => _modelText;
        private set => Set(ref _modelText, value);
    }

    public string UsageText
    {
        get => _usageText;
        private set => Set(ref _usageText, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanSend));
                OnPropertyChanged(nameof(SendVisibility));
                OnPropertyChanged(nameof(StopVisibility));
            }
        }
    }

    public bool CanSend => !_isBusy;

    public Microsoft.UI.Xaml.Visibility SendVisibility =>
        _isBusy ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;

    public Microsoft.UI.Xaml.Visibility StopVisibility =>
        _isBusy ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await DisposeSessionAsync().ConfigureAwait(true);

        Items.Clear();
        _index.Clear();
        _pendingDeltas.Clear();
        UsageText = string.Empty;

        StatusText = "启动中…";
        var error = await _backend.ProbeAsync(cancellationToken).ConfigureAwait(true);
        if (error is not null)
        {
            AddItem(new AgentItem
            {
                Id = $"error:probe:{Guid.NewGuid():n}",
                Kind = AgentItemKind.Error,
                Text = error,
                IsError = true,
            });
            StatusText = "后端不可用";
            return;
        }

        ModelText = _backend.Version ?? "claude";

        var session = _backend.CreateSession(new AgentSessionOptions
        {
            SessionId = Guid.NewGuid().ToString(),
            WorkingDirectory = WorkingDirectory,
        });
        session.EventReceived += OnEventReceived;
        _session = session;

        try
        {
            await session.StartAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AddItem(new AgentItem
            {
                Id = $"error:start:{Guid.NewGuid():n}",
                Kind = AgentItemKind.Error,
                Text = ex.Message,
                IsError = true,
            });
            StatusText = "启动失败";
        }
    }

    public async Task SendAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        // 后端进程异常退出后允许直接重试：重建会话而不是往已关闭的 stdin 写
        if (_session is null || _session.State == AgentSessionState.Failed)
        {
            await StartAsync().ConfigureAwait(true);
        }

        if (_session is null)
        {
            return;
        }

        await _session.SendUserMessageAsync(text.Trim()).ConfigureAwait(true);
    }

    public Task InterruptAsync()
        => _session?.InterruptAsync() ?? Task.CompletedTask;

    public async Task ApproveAsync(TimelineItem item, bool allow)
    {
        if (_session is null || item.Kind != AgentItemKind.Approval)
        {
            return;
        }

        // 乐观更新：避免用户重复点击；终态仍以适配器回报为准
        item.ApprovalState = allow ? ApprovalState.Allowed : ApprovalState.Denied;
        await _session.ResolveApprovalAsync(
            item.Id,
            allow ? ApprovalOutcome.Allow : ApprovalOutcome.Deny).ConfigureAwait(true);
    }

    public async Task ChangeDirectoryAsync(string directory)
    {
        WorkingDirectory = directory;
        await StartAsync().ConfigureAwait(true);
    }

    private void OnEventReceived(AgentEvent evt)
    {
        // 适配器在后台线程读进程，统一切回 UI 线程处理
        _dispatcher.TryEnqueue(() => HandleEvent(evt));
    }

    private void HandleEvent(AgentEvent evt)
    {
        Trace(evt);

        switch (evt)
        {
            case SessionInitEvent init:
                ModelText = string.IsNullOrWhiteSpace(init.Info.Model)
                    ? _backend.Version ?? "claude"
                    : $"{init.Info.Model} · {init.Info.BackendVersion}";
                StatusText = "就绪";
                break;

            case TurnStartedEvent:
                IsBusy = true;
                StatusText = "运行中";
                break;

            case ItemStartedEvent started:
                Upsert(started.Item);
                break;

            case ItemDeltaEvent delta:
                QueueDelta(delta);
                break;

            case ItemCompletedEvent completed:
                Upsert(completed.Item);
                break;

            case ApprovalRequestedEvent approval:
                var approvalVm = AddItem(new AgentItem
                {
                    Id = approval.Request.RequestId,
                    Kind = AgentItemKind.Approval,
                    Text = approval.Request.Title ?? approval.Request.ToolName,
                    Detail = approval.Request.Detail,
                    ToolName = approval.Request.ToolName,
                });
                IsBusy = true;
                StatusText = "等待审批";

                if (AutoApprove && _session is { } autoSession)
                {
                    approvalVm.ApprovalState = ApprovalState.Allowed;
                    _ = autoSession.ResolveApprovalAsync(
                        approval.Request.RequestId,
                        ApprovalOutcome.Allow);
                }

                break;

            case ApprovalResolvedEvent resolved:
                if (_index.TryGetValue(resolved.RequestId, out var approvalItem))
                {
                    approvalItem.ApprovalState = resolved.Outcome == ApprovalOutcome.Allow
                        ? ApprovalState.Allowed
                        : ApprovalState.Denied;
                }

                break;

            case TurnCompletedEvent turn:
                UsageText = $"↑ {turn.Result.InputTokens:N0} · ↓ {turn.Result.OutputTokens:N0} · ${turn.Result.CostUsd:F3}";
                IsBusy = false;
                StatusText = turn.Result.StopReason is null
                    ? "已中断"
                    : turn.Result.IsError ? "本轮出错" : "就绪";
                break;

            case AgentFaultEvent fault:
                AddItem(new AgentItem
                {
                    Id = $"error:{Guid.NewGuid():n}",
                    Kind = AgentItemKind.Error,
                    Text = fault.Error.Message,
                    IsError = true,
                });
                IsBusy = false;
                StatusText = "失败";
                break;

            case SessionStateChangedEvent state:
                StatusText = state.State switch
                {
                    AgentSessionState.Running => "运行中",
                    AgentSessionState.AwaitingApproval => "等待审批",
                    AgentSessionState.Idle => "就绪",
                    AgentSessionState.Interrupted => "已中断",
                    AgentSessionState.Failed => "失败",
                    _ => StatusText,
                };
                break;
        }
    }

    private void Upsert(AgentItem item)
    {
        if (_index.TryGetValue(item.Id, out var existing))
        {
            existing.Apply(item);
        }
        else
        {
            var vm = new TimelineItem(item);
            _index[item.Id] = vm;
            Items.Add(vm);
        }

        ScrollRequested?.Invoke(this, EventArgs.Empty);
    }

    private TimelineItem AddItem(AgentItem item)
    {
        var vm = new TimelineItem(item);
        _index[item.Id] = vm;
        Items.Add(vm);
        ScrollRequested?.Invoke(this, EventArgs.Empty);
        return vm;
    }

    /// <summary>开发期自检：归一化事件落 JSONL。仅用于无人值守断言，不参与产品逻辑。</summary>
    private void Trace(AgentEvent evt)
    {
        if (TracePath is null || evt is ItemDeltaEvent)
        {
            return;
        }

        try
        {
            var record = new
            {
                ts = DateTimeOffset.Now.ToString("HH:mm:ss.fff"),
                evt = evt.GetType().Name,
                state = evt is SessionStateChangedEvent st ? st.State.ToString() : null,
                kind = evt switch
                {
                    ItemStartedEvent i => i.Item.Kind.ToString(),
                    ItemCompletedEvent c => c.Item.Kind.ToString(),
                    _ => null,
                },
                text = evt switch
                {
                    SessionInitEvent si => si.Info.Model,
                    ItemStartedEvent i => i.Item.Text,
                    ItemCompletedEvent c => c.Item.Text,
                    ApprovalRequestedEvent a => $"{a.Request.ToolName} | {a.Request.Detail}",
                    ApprovalResolvedEvent r => r.Outcome.ToString(),
                    TurnCompletedEvent t => $"stop={t.Result.StopReason} err={t.Result.IsError} in={t.Result.InputTokens} out={t.Result.OutputTokens}",
                    AgentFaultEvent f => $"{f.Error.Code}: {f.Error.Message}",
                    _ => null,
                },
                detail = evt is ItemCompletedEvent d ? d.Item.Detail : null,
            };
            File.AppendAllText(TracePath, JsonSerializer.Serialize(record) + Environment.NewLine);
        }
        catch
        {
            // 自检通道，失败不影响主链路
        }
    }

    private void QueueDelta(ItemDeltaEvent delta)
    {
        if (!_pendingDeltas.TryGetValue(delta.ItemId, out var buffer))
        {
            buffer = new StringBuilder();
            _pendingDeltas[delta.ItemId] = buffer;
        }

        buffer.Append(delta.Delta);

        if (!_flushTimer.IsRunning)
        {
            _flushTimer.Start();
        }
    }

    private void FlushDeltas()
    {
        if (_pendingDeltas.Count == 0)
        {
            _flushTimer.Stop();
            return;
        }

        var scrolled = false;
        foreach (var (id, buffer) in _pendingDeltas)
        {
            if (buffer.Length == 0)
            {
                continue;
            }

            var text = buffer.ToString();
            buffer.Clear();

            if (_index.TryGetValue(id, out var item))
            {
                item.Text += text;
                item.IsStreaming = true;
                scrolled = true;
            }
            else
            {
                var vm = new TimelineItem(new AgentItem
                {
                    Id = id,
                    Kind = AgentItemKind.AssistantMessage,
                    Text = text,
                    IsStreaming = true,
                });
                _index[id] = vm;
                Items.Add(vm);
                scrolled = true;
            }
        }

        if (scrolled)
        {
            ScrollRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task DisposeSessionAsync()
    {
        if (_session is { } session)
        {
            session.EventReceived -= OnEventReceived;
            _session = null;
            await session.DisposeAsync().ConfigureAwait(true);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _flushTimer.Stop();
        await DisposeSessionAsync().ConfigureAwait(false);
    }
}
