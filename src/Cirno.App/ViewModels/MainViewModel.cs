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
    private readonly string _providerId;
    private readonly DispatcherQueueTimer _subtitleTimer;

    private SessionViewModel? _active;
    private string? _newSessionModel;

    public MainViewModel(
        string databasePath,
        IEnumerable<IAgentBackend> backends,
        DispatcherQueue dispatcher,
        bool autoApprove,
        string? tracePath,
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

        // 界面上只有一个后端；架构层仍按多后端预留（M3 暂缓）
        _providerId = Orchestrator.Backends.FirstOrDefault()?.Id ?? "claude";

        // 副标题的相对时间要随时间滚动：「14 分钟前」放久了会变成谎言
        _subtitleTimer = dispatcher.CreateTimer();
        _subtitleTimer.Interval = TimeSpan.FromSeconds(60);
        _subtitleTimer.IsRepeating = true;
        _subtitleTimer.Tick += (_, _) =>
        {
            foreach (var session in Sessions)
            {
                session.RefreshTimeDerivedProperties();
            }
        };
        _subtitleTimer.Start();
    }

    public SessionOrchestrator Orchestrator { get; }

    public ObservableCollection<SessionViewModel> Sessions { get; } = [];

    public event EventHandler? ActiveSessionChanged;

    /// <summary>会话状态聚合变化（总数/运行/待审批/失败）。</summary>
    public event Action<SessionSummary>? SummaryChanged;

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

            if (value is not null)
            {
                _ = value.LoadHistoryAsync();
            }

            ActiveSessionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// 从历史目录还原会话列表（权威源），本地库补充运行期字段；不启动任何后端进程。
    /// </summary>
    public async Task RestoreAsync(CancellationToken cancellationToken = default)
    {
        foreach (var record in await Orchestrator.GetSessionsAsync(cancellationToken))
        {
            Sessions.Add(CreateViewModel(record));
        }

        ActiveSession = Sessions.FirstOrDefault();
        RecomputeSummary();
    }

    public SessionViewModel CreateSession(string workingDirectory, string? model = null)
    {
        var record = Orchestrator.CreateSession(_providerId, workingDirectory, model ?? NewSessionModel);
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
    public async Task WriteRestoreReportAsync(string path)
    {
        var reportSessions = new List<object>();
        foreach (var session in Sessions)
        {
            reportSessions.Add(new
            {
                id = session.Id,
                title = session.Title,
                provider = session.Record.ProviderId,
                workingDirectory = session.WorkingDirectory,
                items = (await Orchestrator.LoadItemsAsync(session.Id)).Count,
            });
        }

        var report = new { sessions = reportSessions };
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
        _subtitleTimer.Stop();
        await Orchestrator.DisposeAsync();
        _store.Dispose();
    }
}
