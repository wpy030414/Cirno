namespace Cirno.Shell.Windows;

/// <summary>会话状态的聚合视图。任务栏与托盘只消费这个，不知道「会话」是什么。</summary>
public sealed record ShellStatus(int Total, int Running, int AwaitingApproval, int Failed)
{
    public bool IsIdle => Running == 0 && AwaitingApproval == 0 && Failed == 0;
}

/// <summary>
/// Windows 外壳集成：任务栏进度/徽标、通知区图标、子进程 Job Object。
/// 用法：Attach(hwnd) → 订阅三个请求事件 → 状态变化时调用 Update。
/// </summary>
public sealed class ShellIntegration : IDisposable
{
    private const uint TrayCallbackMessage = NativeMethods.WM_APP + 1;

    private readonly TaskbarIntegration _taskbar = new();
    private readonly JobObject _job = new();
    private readonly uint _taskbarButtonCreated;

    private IntPtr _hwnd;
    private TrayIcon? _tray;
    private NativeMethods.SubclassProc? _subclassProc;
    private IntPtr _badgeIcon;
    private int _badgeCount = -1;
    private ShellStatus _status = new(0, 0, 0, 0);
    private bool _disposed;

    /// <summary>诊断回调：把外壳状态落盘，便于无人值守断言。</summary>
    public Action<string>? Diagnostics { get; set; }

    public ShellIntegration()
    {
        _taskbarButtonCreated = TaskbarIntegration.RegisterCreatedMessage();
    }

    /// <summary>用户在托盘选择「显示 Cirno」。</summary>
    public event Action? ShowRequested;

    /// <summary>用户在托盘选择「新建会话」。</summary>
    public event Action? NewSessionRequested;

    /// <summary>用户在托盘选择「退出」。</summary>
    public event Action? ExitRequested;

    public bool IsTaskbarReady => _taskbar.IsReady;

    /// <summary>把子进程加入作业：Cirno 异常退出时整棵进程树被回收。</summary>
    public bool AssignProcessToJob(int processId) => _job.Assign(processId);

    public void Attach(IntPtr windowHandle)
    {
        if (_hwnd != IntPtr.Zero || windowHandle == IntPtr.Zero)
        {
            return;
        }

        _hwnd = windowHandle;

        _subclassProc = OnWindowMessage;
        NativeMethods.SetWindowSubclass(_hwnd, _subclassProc, 1, IntPtr.Zero);

        _tray = new TrayIcon(
            _hwnd,
            TrayCallbackMessage,
            () => ShowRequested?.Invoke(),
            () => NewSessionRequested?.Invoke(),
            () => ExitRequested?.Invoke());

        var accent = StatusIconFactory.AccentColor();
        var trayVisible = _tray.Show(StatusIconFactory.CreateDot(accent), "Cirno");
        _tray.UpdateTooltip(BuildTooltip());
        Diagnostics?.Invoke($"tray-visible={trayVisible} hwnd=0x{windowHandle.ToInt64():X}");

        // 任务栏按钮可能已经创建过（消息已发过），先尝试直接初始化；
        // 若失败，消息到达时还会再初始化一次。
        _taskbar.Initialize(_hwnd);
        ApplyTaskbar();
    }

    public void Update(ShellStatus status)
    {
        _status = status;
        ApplyTaskbar();
        _tray?.UpdateTooltip(BuildTooltip());
    }

    private void ApplyTaskbar()
    {
        if (_hwnd == IntPtr.Zero || !_taskbar.IsReady)
        {
            return;
        }

        var (state, completed, total) = _status switch
        {
            { AwaitingApproval: > 0 } => (TaskbarProgressState.Paused, 0UL, 0UL),
            { Failed: > 0 } => (TaskbarProgressState.Error, 0UL, 0UL),
            { Running: > 0 } => (TaskbarProgressState.Indeterminate, 0UL, 0UL),
            _ => (TaskbarProgressState.NoProgress, 0UL, 0UL),
        };

        var progressApplied = _taskbar.SetProgress(_hwnd, state, completed, total);

        if (_status.AwaitingApproval > 0)
        {
            if (_badgeCount != _status.AwaitingApproval)
            {
                StatusIconFactory.Destroy(_badgeIcon);
                _badgeIcon = StatusIconFactory.CreateBadge(_status.AwaitingApproval, StatusIconFactory.AccentColor());
                _badgeCount = _status.AwaitingApproval;
            }

            var overlayApplied = _taskbar.SetOverlay(_hwnd, _badgeIcon, $"{_status.AwaitingApproval} 个会话等待审批");
            Diagnostics?.Invoke($"taskbar-ready={_taskbar.IsReady} progress={state} progress-ok={progressApplied} badge={_status.AwaitingApproval} overlay-ok={overlayApplied}");
        }
        else if (_badgeCount >= 0)
        {
            StatusIconFactory.Destroy(_badgeIcon);
            _badgeIcon = IntPtr.Zero;
            _badgeCount = -1;
            var cleared = _taskbar.SetOverlay(_hwnd, IntPtr.Zero, string.Empty);
            Diagnostics?.Invoke($"taskbar-ready={_taskbar.IsReady} progress={state} progress-ok={progressApplied} badge=0 overlay-ok={cleared}");
        }
    }

    private string BuildTooltip()
    {
        if (_status.IsIdle)
        {
            return _status.Total == 0 ? "Cirno — 尚无会话" : "Cirno — 就绪";
        }

        var parts = new List<string>();
        if (_status.AwaitingApproval > 0)
        {
            parts.Add($"{_status.AwaitingApproval} 个待审批");
        }

        if (_status.Running > 0)
        {
            parts.Add($"{_status.Running} 个运行中");
        }

        if (_status.Failed > 0)
        {
            parts.Add($"{_status.Failed} 个失败");
        }

        return "Cirno — " + string.Join(" · ", parts);
    }

    private IntPtr OnWindowMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, IntPtr refData)
    {
        if (msg == _taskbarButtonCreated)
        {
            _taskbar.Initialize(hWnd);
            ApplyTaskbar();
            return IntPtr.Zero;
        }

        if (_tray?.HandleMessage(msg, wParam, lParam) == true)
        {
            return IntPtr.Zero;
        }

        if (msg == NativeMethods.WM_DESTROY)
        {
            _taskbar.Dispose(hWnd);
        }

        return NativeMethods.DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_hwnd != IntPtr.Zero && _subclassProc is not null)
        {
            NativeMethods.RemoveWindowSubclass(_hwnd, _subclassProc, 1);
            _taskbar.Dispose(_hwnd);
            _hwnd = IntPtr.Zero;
        }

        _tray?.Dispose();
        _tray = null;

        StatusIconFactory.Destroy(_badgeIcon);
        _badgeIcon = IntPtr.Zero;

        // 释放作业句柄 = 杀掉仍在运行的子进程树
        _job.Dispose();
    }
}
