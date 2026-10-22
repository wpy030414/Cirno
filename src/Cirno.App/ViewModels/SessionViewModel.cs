using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using Cirno.Agents.Abstractions;
using Cirno.Core.Models;
using Cirno.Core.Orchestration;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Cirno.App.ViewModels;

/// <summary>
/// 单个会话的视图模型：把编排层的归一化事件转成时间线条目。
/// 流式增量按固定帧率合帧提交，避免每个 token 都触发一次布局。
/// </summary>
public sealed class SessionViewModel : ObservableObject
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(33);

    private readonly SessionOrchestrator _orchestrator;
    private readonly DispatcherQueue _dispatcher;
    private readonly Dictionary<string, TimelineItem> _index = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StringBuilder> _pendingDeltas = new(StringComparer.Ordinal);
    private readonly DispatcherQueueTimer _flushTimer;

    private SessionRecord _record;
    private string _statusText = "空闲";
    private string _modelText;
    private string _usageText = string.Empty;
    private bool _isBusy;
    private bool _historyLoaded;

    public SessionViewModel(
        SessionOrchestrator orchestrator,
        SessionRecord record,
        DispatcherQueue dispatcher,
        bool autoApprove,
        string? tracePath)
    {
        _orchestrator = orchestrator;
        _record = record;
        _dispatcher = dispatcher;
        AutoApprove = autoApprove;
        TracePath = tracePath;
        _modelText = record.Model ?? "—";

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

    public string Id => _record.Id;

    public SessionRecord Record => _record;

    public string Title => string.IsNullOrWhiteSpace(_record.Title) ? "新会话" : _record.Title!;

    public string WorkingDirectory => _record.WorkingDirectory;

    public string ProviderLabel => _record.ProviderId switch
    {
        "claude" => "Claude Code",
        "codex" => "Codex",
        _ => _record.ProviderId,
    };

    public string SubtitleLine => $"{ProviderLabel} · {_statusText}";

    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (Set(ref _statusText, value))
            {
                OnPropertyChanged(nameof(SubtitleLine));
            }
        }
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

    public Visibility SendVisibility => _isBusy ? Visibility.Collapsed : Visibility.Visible;

    public Visibility StopVisibility => _isBusy ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>惰性加载历史：只有被选中过的会话才读库。</summary>
    public void LoadHistory()
    {
        if (_historyLoaded)
        {
            return;
        }

        _historyLoaded = true;
        foreach (var item in _orchestrator.LoadItems(Id))
        {
            var vm = new TimelineItem(item);
            _index[item.Id] = vm;
            Items.Add(vm);
        }
    }

    public void ApplyRecord(SessionRecord record)
    {
        _record = record;
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(WorkingDirectory));
        OnPropertyChanged(nameof(ProviderLabel));
        OnPropertyChanged(nameof(SubtitleLine));
        if (!string.IsNullOrWhiteSpace(record.Model))
        {
            ModelText = record.Model;
        }
    }

    public async Task SendAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        try
        {
            await _orchestrator.SendAsync(Id, text.Trim());
        }
        catch (Exception ex)
        {
            AddItem(new AgentItem
            {
                Id = $"error:send:{Guid.NewGuid():n}",
                Kind = AgentItemKind.Error,
                Text = ex.Message,
                IsError = true,
            });
            IsBusy = false;
            StatusText = "失败";
        }
    }

    public Task InterruptAsync() => _orchestrator.InterruptAsync(Id);

    public async Task ApproveAsync(TimelineItem item, bool allow)
    {
        if (item.Kind != AgentItemKind.Approval)
        {
            return;
        }

        // 乐观更新：避免用户重复点击；终态仍以适配器回报为准
        item.ApprovalState = allow ? ApprovalState.Allowed : ApprovalState.Denied;
        await _orchestrator.ResolveApprovalAsync(
            Id,
            item.Id,
            allow ? ApprovalOutcome.Allow : ApprovalOutcome.Deny);
    }

    /// <summary>由 MainViewModel 在 UI 线程调用。</summary>
    public void HandleEvent(AgentEvent evt)
    {
        Trace(evt);

        switch (evt)
        {
            case SessionInitEvent init:
                if (!string.IsNullOrWhiteSpace(init.Info.Model))
                {
                    ModelText = init.Info.Model!;
                }

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
            {
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

                if (AutoApprove)
                {
                    approvalVm.ApprovalState = ApprovalState.Allowed;
                    _ = _orchestrator.ResolveApprovalAsync(
                        Id,
                        approval.Request.RequestId,
                        ApprovalOutcome.Allow);
                }

                break;
            }

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
                    : turn.Result.IsError ? "本轮出错" : "空闲";
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
                ApplyState(state.State);
                break;
        }
    }

    /// <summary>编排层状态（排队等）。</summary>
    public void ApplyState(AgentSessionState state)
    {
        StatusText = state switch
        {
            AgentSessionState.Queued => "等待中",
            AgentSessionState.Running => "运行中",
            AgentSessionState.AwaitingApproval => "等待审批",
            AgentSessionState.Idle => "空闲",
            AgentSessionState.Interrupted => "已中断",
            AgentSessionState.Failed => "失败",
            AgentSessionState.Starting => "启动中",
            _ => StatusText,
        };

        if (state is AgentSessionState.Running or AgentSessionState.AwaitingApproval)
        {
            IsBusy = true;
        }
        else if (state is AgentSessionState.Failed or AgentSessionState.Interrupted
                 or AgentSessionState.Idle)
        {
            IsBusy = false;
        }
    }

    private void Upsert(AgentItem item)
    {
        if (_index.TryGetValue(item.Id, out var existing))
        {
            existing.Apply(item);
            return;
        }

        var vm = new TimelineItem(item);
        _index[item.Id] = vm;
        Items.Add(vm);
    }

    private TimelineItem AddItem(AgentItem item)
    {
        if (_index.TryGetValue(item.Id, out var existing))
        {
            existing.Apply(item);
            return existing;
        }

        var vm = new TimelineItem(item);
        _index[item.Id] = vm;
        Items.Add(vm);
        return vm;
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
            }
        }
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
                session = Id,
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
}
