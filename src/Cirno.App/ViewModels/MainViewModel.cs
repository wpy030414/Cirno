using System.Collections.ObjectModel;
using System.Text.Json;
using Cirno.Agents.Abstractions;
using Cirno.Core.Orchestration;
using Cirno.Core.Persistence;
using Microsoft.UI.Dispatching;

namespace Cirno.App.ViewModels;

/// <summary>会话状态聚合，供任务栏/托盘呈现。界面之外的消费者只需要这几个数字。</summary>
public sealed record SessionSummary(int Total, int Running, int AwaitingApproval, int Failed);

/// <summary>
/// 应用级视图模型：会话列表、当前会话、新建/删除。
/// 编排与持久化都在 Core 里，这里只负责把状态搬到界面。
/// </summary>
public sealed class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly SessionStore _store;
    private readonly DispatcherQueue _dispatcher;
    private readonly bool _autoApprove;
    private readonly string? _tracePath;

    private SessionViewModel? _active;
    private string _defaultProviderId;
    private string? _newSessionModel;

    public MainViewModel(
        string databasePath,
        IEnumerable<IAgentBackend> backends,
        DispatcherQueue dispatcher,
        bool autoApprove,
        string? tracePath,
        string? preferredProviderId = null,
        Action<int>? onProcessStarted = null)
    {
        _dispatcher = dispatcher;
        _autoApprove = autoApprove;
        _tracePath = tracePath;

        _store = new SessionStore(databasePath);
        Orchestrator = new SessionOrchestrator(_store, backends, onProcessStarted);
        Orchestrator.SessionEvent += OnSessionEvent;
        Orchestrator.SessionChanged += OnSessionChanged;
        Orchestrator.SessionStateChanged += OnSessionStateChanged;

        _defaultProviderId = preferredProviderId is not null && Orchestrator.GetBackend(preferredProviderId) is not null
            ? preferredProviderId
            : Orchestrator.Backends.FirstOrDefault()?.Id ?? "claude";
    }

    public SessionOrchestrator Orchestrator { get; }

    public ObservableCollection<SessionViewModel> Sessions { get; } = [];

    public IReadOnlyList<IAgentBackend> Backends => Orchestrator.Backends;

    public event EventHandler? ActiveSessionChanged;

    /// <summary>会话状态聚合变化（总数/运行/待审批/失败）。</summary>
    public event Action<SessionSummary>? SummaryChanged;

    public string DefaultProviderId
    {
        get => _defaultProviderId;
        set
        {
            if (Set(ref _defaultProviderId, value))
            {
                OnPropertyChanged(nameof(DefaultProviderLabel));
            }
        }
    }

    public string DefaultProviderLabel
        => Orchestrator.GetBackend(_defaultProviderId)?.DisplayName ?? _defaultProviderId;

    /// <summary>新建会话使用的模型；null 表示跟随后端默认。</summary>
    public string? NewSessionModel
    {
        get => _newSessionModel;
        set => Set(ref _newSessionModel, value);
    }

    public SessionViewModel? ActiveSession
    {
        get => _active;
        set
        {
            if (!Set(ref _active, value))
            {
                return;
            }

            value?.LoadHistory();
            ActiveSessionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>从本地库还原会话列表；不启动任何后端进程。</summary>
    public void Restore()
    {
        foreach (var record in Orchestrator.GetSessions())
        {
            Sessions.Add(CreateViewModel(record));
        }

        ActiveSession = Sessions.FirstOrDefault();
        RecomputeSummary();
    }

    public SessionViewModel CreateSession(
        string workingDirectory,
        string? providerId = null,
        string? model = null)
    {
        var record = Orchestrator.CreateSession(
            providerId ?? DefaultProviderId,
            workingDirectory,
            model ?? NewSessionModel);
        var vm = CreateViewModel(record);
        Sessions.Insert(0, vm);
        ActiveSession = vm;
        RecomputeSummary();
        return vm;
    }

    public async Task DeleteAsync(SessionViewModel session)
    {
        await Orchestrator.DeleteAsync(session.Id);
        session.StateChanged -= RecomputeSummary;
        Sessions.Remove(session);
        RecomputeSummary();

        if (ReferenceEquals(ActiveSession, session))
        {
            ActiveSession = Sessions.FirstOrDefault();
        }
    }

    /// <summary>新建会话时沿用最近一个会话的目录，没有就用当前目录。</summary>
    public string ResolveWorkingDirectory()
        => Sessions.FirstOrDefault()?.WorkingDirectory ?? Environment.CurrentDirectory;

    /// <summary>开发期自检：把还原结果落成 JSON，用于无人值守断言。</summary>
    public void WriteRestoreReport(string path)
    {
        var report = new
        {
            sessions = Sessions.Select(s => new
            {
                id = s.Id,
                title = s.Title,
                provider = s.Record.ProviderId,
                workingDirectory = s.WorkingDirectory,
                items = Orchestrator.LoadItems(s.Id).Count,
            }),
        };

        File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    private SessionViewModel CreateViewModel(Cirno.Core.Models.SessionRecord record)
    {
        var vm = new SessionViewModel(Orchestrator, record, _dispatcher, _autoApprove, _tracePath);
        vm.StateChanged += RecomputeSummary;
        return vm;
    }

    private void RecomputeSummary()
    {
        var running = Sessions.Count(s =>
            s.State is AgentSessionState.Running or AgentSessionState.Queued);
        var awaiting = Sessions.Count(s => s.State == AgentSessionState.AwaitingApproval);
        var failed = Sessions.Count(s => s.State == AgentSessionState.Failed);

        SummaryChanged?.Invoke(new SessionSummary(Sessions.Count, running, awaiting, failed));
    }

    private void OnSessionEvent(string sessionId, AgentEvent evt)
    {
        _dispatcher.TryEnqueue(() => Find(sessionId)?.HandleEvent(evt));
    }

    private void OnSessionChanged(string sessionId)
    {
        var record = _store.GetSession(sessionId);
        if (record is null)
        {
            return;
        }

        _dispatcher.TryEnqueue(() => Find(sessionId)?.ApplyRecord(record));
    }

    private void OnSessionStateChanged(string sessionId, AgentSessionState state)
    {
        _dispatcher.TryEnqueue(() => Find(sessionId)?.ApplyState(state));
    }

    private SessionViewModel? Find(string sessionId)
        => Sessions.FirstOrDefault(s => s.Id == sessionId);

    public async ValueTask DisposeAsync()
    {
        await Orchestrator.DisposeAsync();
        _store.Dispose();
    }
}
