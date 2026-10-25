using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using Cirno.Agents.Claude;
using Cirno.App.Services;
using Cirno.App.ViewModels;
using Cirno.Shell.Windows;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI.Core;

namespace Cirno.App;

public sealed partial class MainWindow : Window
{
    private readonly ThemeService _themes = new();
    private readonly ShellIntegration _shell = new();
    private readonly MainViewModel _main;
    private readonly SingleInstance? _instance;
    private SessionViewModel? _active;
    private bool _isDark;
    private bool _ready;
    private bool _suppressSelection;

    /// <summary>
    /// 侧栏导航项：镜像 MainViewModel 的会话集合。
    /// NavigationView 的 MenuItemsSource 只允许由 XAML 绑定赋值，代码里直接赋值会崩
    /// （WinUI 原生层 0xC0000005），所以这里维护一份镜像并用 x:Bind 单向绑定。
    /// </summary>
    public ObservableCollection<object> Sessions { get; } = [];

    public MainWindow(SingleInstance? instance = null)
    {
        InitializeComponent();

        _instance = instance;
        Title = "Cirno";
        ApplyBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.Resize(new SizeInt32(1280, 860));

        if (AppWindowTitleBar.IsCustomizationSupported())
        {
            AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
            AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        }

        _isDark = Application.Current.RequestedTheme == ApplicationTheme.Dark;
        ModeToggle.IsOn = _isDark;
        ApplyElementTheme();

        ThemePicker.ItemsSource = _themes.Themes;

        var databasePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Cirno",
            "cirno.db");

        // 外壳集成：任务栏/托盘/子进程托管。必须在窗口句柄就绪之后。
        if (ReadArgument("--trace") is { } shellTrace)
        {
            _shell.Diagnostics = message => AppendShellTrace(shellTrace, message);
        }

        _shell.Attach(WinRT.Interop.WindowNative.GetWindowHandle(this));

        _main = new MainViewModel(
            databasePath,
            [new ClaudeBackend()],
            DispatcherQueue,
            autoApprove: HasFlag("--auto-approve"),
            tracePath: ReadArgument("--trace"),
            onProcessStarted: pid =>
            {
                var assigned = _shell.AssignProcessToJob(pid);
                if (ReadArgument("--trace") is { } tracePath)
                {
                    AppendShellTrace(tracePath, $"job-assign pid={pid} ok={assigned}");
                }
            });

        _main.SummaryChanged += summary =>
        {
            _shell.Update(new ShellStatus(
                summary.Total, summary.Running, summary.AwaitingApproval, summary.Failed));
        };

        // 事件在 _main 就绪之后再挂，避免托盘先于视图模型可用（也消掉可空警告）
        _shell.ShowRequested += RestoreWindow;
        _shell.NewSessionRequested += () =>
        {
            RestoreWindow();
            BeginDraft();
        };
        _shell.ExitRequested += Close;

        _main.ActiveSessionChanged += OnActiveSessionChanged;
        _main.Sessions.CollectionChanged += OnSessionsChanged;
        foreach (var session in _main.Sessions)
        {
            Sessions.Add(session);
        }

        // 界面不再提供模型选择器；--model 仍是新建会话指定模型的唯一入口（开发/自检用）
        _main.NewSessionModel = ReadArgument("--model");

        RegisterAccelerators();

        var requestedId = ReadArgument("--theme");
        var initial = _themes.Themes.FirstOrDefault(
                          t => string.Equals(t.Id, requestedId, StringComparison.OrdinalIgnoreCase))
                      ?? _themes.Themes[0];

        ThemePicker.SelectedItem = initial;
        _ready = true;
        ApplyTheme(initial);

        CommitJumpList();

        // 会话列表在 OnRootLoaded 里异步还原：扫描历史目录不该挡住首帧
        OnActiveSessionChanged(null, EventArgs.Empty);

        RootGrid.Loaded += OnRootLoaded;
        Closed += OnWindowClosed;
    }

    /// <summary>开发期自检：万条消息的界面性能探针需要直接操作时间线与编排层。</summary>
    public ListView TimelineControl => Timeline;

    /// <summary>开发期自检：性能探针要往库里造数据。</summary>
    public Cirno.Core.Orchestration.SessionOrchestrator? PerfOrchestrator => _main.Orchestrator;

    private async void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        RootGrid.Loaded -= OnRootLoaded;
        Composer.Focus(FocusState.Programmatic);

        // 先还原会话列表（历史目录权威源，后台扫描），再跑自检链路
        await _main.RestoreAsync();

        // 开发期自检：还原结果落成 JSON
        if (ReadArgument("--restore-check") is { } reportPath)
        {
            await _main.WriteRestoreReportAsync(reportPath);
        }

        // 开发期自检：万条消息的界面性能探针
        if (int.TryParse(ReadArgument("--perf"), out var perfItems) && perfItems > 0)
        {
            await RunPerfProbeAsync(perfItems);
            return;
        }

        var prompt = ReadArgument("--prompt");
        if (string.IsNullOrWhiteSpace(prompt))
        {
            // 冷启动时任务栏跳转列表的「新建会话」：进入草稿，不提前占侧栏
            if (HasFlag("--new-session"))
            {
                BeginDraft(ReadArgument("--cwd"));
            }

            return;
        }

        // 自检：--parallel N 同时向 N 个会话发同一问题，验证并发不串流
        if (int.TryParse(ReadArgument("--parallel"), out var parallel) && parallel > 1)
        {
            var root = Path.Combine(Path.GetTempPath(), "cirno-parallel");
            var sends = new List<Task>();
            for (var i = 0; i < parallel; i++)
            {
                var directory = Path.Combine(root, $"s{i}");
                Directory.CreateDirectory(directory);
                var session = _main.CreateSession(directory);
                sends.Add(session.SendAsync($"{prompt} (会话 {i})"));
            }

            await Task.WhenAll(sends);
            return;
        }

        // 自检：--new-session 强制新建；发送时才真正落库（与界面草稿语义一致）
        if (_main.ActiveSession is null || HasFlag("--new-session"))
        {
            BeginDraft(ReadArgument("--cwd"));
        }

        Composer.Text = prompt;
        await SendAsync();

        if (int.TryParse(ReadArgument("--stop-after"), out var seconds) && seconds > 0)
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds));
            await (_main.ActiveSession?.InterruptAsync() ?? Task.CompletedTask);
        }
    }

    /// <summary>
    /// 开发期自检：造一个万条消息的会话，测还原/绑定/滚动/内存，报告落 JSON 后退出。
    /// </summary>
    private async Task RunPerfProbeAsync(int itemCount)
    {
        var probe = new PerfProbe(this, itemCount);
        var report = await probe.RunAsync();

        var reportPath = ReadArgument("--perf-report")
                         ?? Path.Combine(Path.GetTempPath(), "cirno-perf-ui.json");
        File.WriteAllText(reportPath, report.ToJson());

        if (ReadArgument("--trace") is { } tracePath)
        {
            AppendShellTrace(tracePath, $"perf items={report.ItemCount} seed={report.SeedMs:F0}ms " +
                                        $"restore={report.RestoreMs:F0}ms bind={report.BindMs:F0}ms " +
                                        $"scroll={report.ScrollToEndMs:F0}ms heap={report.ManagedHeapBytes / 1024 / 1024:F1}MB");
        }

        Close();
    }

    private async void OnWindowClosed(object sender, WindowEventArgs args)
    {
        // 先优雅释放会话（回绝挂起审批、关 stdin），再由 Job Object 兜底清理进程树
        await _main.DisposeAsync();
        _shell.Dispose();
    }

    private void RestoreWindow()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.Restore();
        }

        Activate();
    }

    /// <summary>处理来自第二个实例的启动意图（跳转列表、命令行复用等）。</summary>
    public async void HandleActivation(ActivationRequest request)
    {
        if (ReadArgument("--trace") is { } activationTrace)
        {
            AppendShellTrace(
                activationTrace,
                $"activation new={request.NewSession} cwd={request.WorkingDirectory ?? "none"} prompt={(request.Prompt is null ? "none" : "yes")}");
        }

        if (request.BringToFront)
        {
            RestoreWindow();
        }

        if (request.NewSession)
        {
            // 新建意图 = 进入草稿；带 prompt 时由 SendAsync 在发送瞬间落库
            BeginDraft(request.WorkingDirectory);
        }
        else if (_main.ActiveSession is null && request.WorkingDirectory is not null)
        {
            BeginDraft(request.WorkingDirectory);
        }

        if (!string.IsNullOrWhiteSpace(request.Prompt))
        {
            Composer.Text = request.Prompt;
            await SendAsync();
        }
    }

    // ---------- 会话切换 ----------

    private void OnActiveSessionChanged(object? sender, EventArgs e)
    {
        if (_active is not null)
        {
            _active.Items.CollectionChanged -= OnItemsChanged;
        }

        _active = _main.ActiveSession;

        if (_active is not null)
        {
            _active.Items.CollectionChanged += OnItemsChanged;
        }

        Timeline.ItemsSource = _active?.Items;
        SessionHost.DataContext = _active;
        Title = _active is null ? "Cirno" : $"Cirno — {_active.Title}";

        SyncSelection();
        UpdateDraftUi();
        UpdateEmptyState();
        ScrollToEnd(force: true);
    }

    private void SyncSelection()
    {
        _suppressSelection = true;
        SessionNav.SelectedItem = _active;
        _suppressSelection = false;
    }

    private void OnSessionSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs e)
    {
        if (!_suppressSelection && SessionNav.SelectedItem is SessionViewModel vm && !ReferenceEquals(vm, _active))
        {
            _main.ActiveSession = vm;
        }
    }

    private void OnNewSessionClick(object sender, RoutedEventArgs e) => BeginDraft();

    /// <summary>
    /// 进入草稿态并聚焦输入框：不创建会话、不写库，首条消息发出时才落库入列。
    /// </summary>
    private void BeginDraft(string? workingDirectory = null)
    {
        _main.BeginDraft(workingDirectory);
        Composer.Focus(FocusState.Programmatic);
    }

    /// <summary>草稿态下的工作目录显示与显隐；会话开始后控件永远不再出现。</summary>
    private void UpdateDraftUi()
    {
        var draft = _active is null;
        DraftFolderButton.Visibility = draft ? Visibility.Visible : Visibility.Collapsed;

        var directory = _active?.WorkingDirectory ?? _main.DraftWorkingDirectory;
        if (draft)
        {
            DraftFolderText.Text = string.IsNullOrWhiteSpace(directory)
                ? "选择文件夹"
                : directory;
            ToolTipService.SetToolTip(DraftFolderButton, directory);
        }

        ToolTipService.SetToolTip(
            OpenFolderButton,
            string.IsNullOrWhiteSpace(directory)
                ? "在资源管理器中打开工作目录"
                : $"在资源管理器中打开：{directory}");
    }

    /// <summary>键盘全流程：新建、切会话、中断、聚焦输入，都不需要鼠标。</summary>
    private void RegisterAccelerators()
    {
        AddAccelerator(VirtualKey.N, VirtualKeyModifiers.Control, () => BeginDraft());
        AddAccelerator(VirtualKey.K, VirtualKeyModifiers.Control, () => Composer.Focus(FocusState.Programmatic));
        AddAccelerator(VirtualKey.Escape, VirtualKeyModifiers.None, () =>
        {
            if (_active is { IsBusy: true })
            {
                _ = _active.InterruptAsync();
            }
        });

        for (var i = 1; i <= 9; i++)
        {
            var index = i;
            AddAccelerator(
                VirtualKey.Number1 + (i - 1),
                VirtualKeyModifiers.Control,
                () => SelectSession(index));
        }
    }

    private void AddAccelerator(VirtualKey key, VirtualKeyModifiers modifiers, Action action)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accelerator.Invoked += (_, args) =>
        {
            args.Handled = true;
            action();
        };
        RootGrid.KeyboardAccelerators.Add(accelerator);
    }

    private void SelectSession(int oneBasedIndex)
    {
        if (oneBasedIndex >= 1 && oneBasedIndex <= _main.Sessions.Count)
        {
            _main.ActiveSession = _main.Sessions[oneBasedIndex - 1];
        }
    }

    /// <summary>草稿态选择工作目录：只改草稿参数，不创建会话。</summary>
    private async void OnPickDraftFolderClick(object sender, RoutedEventArgs e)
    {
        if (_active is not null)
        {
            return;
        }

        var path = await PickFolderAsync();
        if (path is null)
        {
            return;
        }

        _main.SetDraftWorkingDirectory(path);
        UpdateDraftUi();
        Composer.Focus(FocusState.Programmatic);
    }

    private async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    /// <summary>右上角文件夹按钮：在资源管理器里打开当前会话（或草稿）的工作目录。</summary>
    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        var path = _active?.WorkingDirectory ?? _main.DraftWorkingDirectory;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            _ = ShowErrorAsync("无法打开工作目录", "目录不存在或不可访问，请重新选择文件夹。");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe")
            {
                UseShellExecute = true,
                ArgumentList = { path },
            });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            _ = ShowErrorAsync("无法打开工作目录", ex.Message);
        }
    }

    /// <summary>窗口尚未 Loaded（激活早期）时 XamlRoot 可能为空，此时没有可呈现错误的位置，直接放弃。</summary>
    private async Task ShowErrorAsync(string title, string message)
    {
        if (RootGrid.XamlRoot is null)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = title,
            Content = message,
            CloseButtonText = "知道了",
            DefaultButton = ContentDialogButton.Close,
        };

        await dialog.ShowAsync();
    }

    private async void OnDeleteSessionClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SessionViewModel vm)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "删除会话",
            Content = $"将删除会话「{vm.Title}」：历史文件移入回收站，本地记录与时间线一并清除。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            await _main.DeleteAsync(vm);
        }
        catch (Exception ex)
        {
            // 历史文件移入回收站失败等：会话原样保留，用户可重试
            var failure = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = "删除失败",
                Content = ex.Message,
                CloseButtonText = "知道了",
                DefaultButton = ContentDialogButton.Close,
            };
            await failure.ShowAsync();
        }
    }

    // ---------- 发送与中断 ----------

    private async void OnSendClick(object sender, RoutedEventArgs e) => await SendAsync();

    private async void OnComposerKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
        {
            return;
        }

        var shift = InputKeyboardSource
            .GetKeyStateForCurrentThread(VirtualKey.Shift)
            .HasFlag(CoreVirtualKeyStates.Down);

        if (shift)
        {
            return;
        }

        e.Handled = true;
        await SendAsync();
    }

    private async Task SendAsync()
    {
        if (_active is { IsBusy: true })
        {
            return;
        }

        var text = Composer.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        // 草稿态：首条消息才是会话的诞生时刻——此刻落库、入列，然后发送
        var target = _active;
        if (target is null)
        {
            var directory = _main.DraftWorkingDirectory;
            if (!Directory.Exists(directory))
            {
                await ShowErrorAsync("无法新建会话", "工作目录不存在，请重新选择文件夹。");
                return;
            }

            try
            {
                target = _main.CreateSession(directory);
            }
            catch (Exception ex)
            {
                // 落库失败：保留输入与草稿状态，用户可以重试
                await ShowErrorAsync("无法新建会话", ex.Message);
                return;
            }
        }

        Composer.Text = string.Empty;
        await target.SendAsync(text);
    }

    private async void OnStopClick(object sender, RoutedEventArgs e)
    {
        if (_active is not null)
        {
            await _active.InterruptAsync();
        }
    }

    private async void OnApproveClick(object sender, RoutedEventArgs e)
    {
        if (_active is not null && sender is FrameworkElement { DataContext: TimelineItem item })
        {
            await _active.ApproveAsync(item, allow: true);
        }
    }

    private async void OnDenyClick(object sender, RoutedEventArgs e)
    {
        if (_active is not null && sender is FrameworkElement { DataContext: TimelineItem item })
        {
            await _active.ApproveAsync(item, allow: false);
        }
    }

    // ---------- 时间线滚动 ----------

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateEmptyState();

        // 历史批量导入是一次 Reset（BulkObservableCollection.AddRange）：
        // 条目就位后强制落到底部，否则切进会话会停在最顶部。
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            ScrollToEnd(force: true);
        }
    }

    private void UpdateEmptyState()
    {
        var empty = _active is null || _active.Items.Count == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 跟随滚动：只在新内容到达时贴底；用户回看历史时不抢滚动位置。
    /// force = 进入会话/历史导入时直接落到底部。
    /// </summary>
    private void ScrollToEnd(bool force = false)
    {
        if (Timeline.Items.Count == 0)
        {
            return;
        }

        var scrollViewer = FindScrollViewer(Timeline);
        if (scrollViewer is null)
        {
            return;
        }

        if (!force &&
            scrollViewer.ScrollableHeight - scrollViewer.VerticalOffset > 240)
        {
            return;
        }

        Timeline.ScrollIntoView(Timeline.Items[^1]);

        if (!force)
        {
            return;
        }

        // Reset 之后布局还没完成，ScrollableHeight 仍是旧值；等一帧再顶到底
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            var viewer = FindScrollViewer(Timeline);
            viewer?.ChangeView(null, viewer.ScrollableHeight, null, disableAnimation: true);
        });
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer viewer)
            {
                return viewer;
            }

            if (FindScrollViewer(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    // ---------- 主题 ----------

    private void OnThemeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ready && ThemePicker.SelectedItem is ThemeEntry entry)
        {
            ApplyTheme(entry);
        }
    }

    private void ApplyTheme(ThemeEntry entry)
    {
        _themes.Apply(entry.Id);
        UpdateArt();
    }

    private static string? ReadArgument(string name)
    {
        var args = Environment.GetCommandLineArgs();
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    /// <summary>开发期自检：外壳状态与归一化事件写同一个 trace 流。</summary>
    private static void AppendShellTrace(string path, string message)
    {
        try
        {
            File.AppendAllText(
                path,
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    ts = DateTimeOffset.Now.ToString("HH:mm:ss.fff"),
                    session = "shell",
                    evt = "ShellState",
                    text = message,
                }) + Environment.NewLine);
        }
        catch
        {
            // 自检通道，失败不影响主链路
        }
    }

    private static bool HasFlag(string name)
        => Environment.GetCommandLineArgs()
            .Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>提交跳转列表：任务栏右键菜单里的「新建会话」。失败不影响主功能。</summary>
    private void CommitJumpList()
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            return;
        }

        var jumpList = new JumpListService(executable);
        var tracePath = ReadArgument("--trace");
        if (tracePath is not null)
        {
            jumpList.Trace = message => AppendShellTrace(tracePath, message);
        }

        var committed = jumpList.TryCommit();
        if (tracePath is not null)
        {
            AppendShellTrace(tracePath, $"jumplist-committed={committed} error={jumpList.LastError ?? "none"}");
        }
    }

    private void OnModeToggled(object sender, RoutedEventArgs e)
    {
        _isDark = ModeToggle.IsOn;
        ApplyElementTheme();
        UpdateArt();
    }

    /// <summary>
    /// 内容区最宽 CirnoContentMaxWidth：窗口更宽时居中留白，不把正文拉到难以阅读的长度。
    /// </summary>
    private void OnContentSizeChanged(object sender, SizeChangedEventArgs e)
    {
        SessionHost.Width = double.IsFinite(SessionHost.MaxWidth)
            ? Math.Min(e.NewSize.Width, SessionHost.MaxWidth)
            : e.NewSize.Width;

        ResizeCharacters();
    }

    private void OnSessionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add when e.NewItems is not null:
            {
                var index = e.NewStartingIndex < 0 ? Sessions.Count : e.NewStartingIndex;
                foreach (var item in e.NewItems)
                {
                    Sessions.Insert(index++, item!);
                }

                break;
            }

            case NotifyCollectionChangedAction.Remove when e.OldItems is not null:
                foreach (var item in e.OldItems)
                {
                    Sessions.Remove(item!);
                }

                break;

            case NotifyCollectionChangedAction.Reset:
                Sessions.Clear();
                foreach (var session in _main.Sessions)
                {
                    Sessions.Add(session);
                }

                break;
        }
    }

    /// <summary>
    /// 材质：Mica 可用时隐藏纯色回退层；不可用（Win10 或远程会话）时保留纯色，
    /// 否则根元素透明会让深色下的文字落在白底上，整页不可读。
    /// </summary>
    private void ApplyBackdrop()
    {
        if (MicaController.IsSupported())
        {
            SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
            PageBackground.Visibility = Visibility.Collapsed;
        }
        else
        {
            SystemBackdrop = null;
            PageBackground.Visibility = Visibility.Visible;
        }
    }

    private void ApplyElementTheme()
    {
        RootGrid.RequestedTheme = _isDark ? ElementTheme.Dark : ElementTheme.Light;
    }

    /// <summary>按当前主题装配艺术层；默认主题时不加载任何图片。</summary>
    private void UpdateArt()
    {
        if (_themes.Art is not { } art)
        {
            ArtLayer.Visibility = Visibility.Collapsed;
            BackdropImage.Source = null;
            CharacterLeftImage.Source = null;
            CharacterRightImage.Source = null;
            ThemeStatusText.Text = "主题：默认（Fluent，不加载图片）";
            return;
        }

        ArtLayer.Visibility = Visibility.Visible;
        BackdropImage.Source = art.BackgroundFor(_isDark);
        BackdropImage.Opacity = art.OverlayOpacity;
        CharacterLeftImage.Source = art.CharacterLeft;
        CharacterRightImage.Source = art.CharacterRight;
        CharacterLeftImage.Opacity = art.CharacterOpacity;
        CharacterRightImage.Opacity = art.CharacterOpacity;
        ThemeStatusText.Text = $"主题：{_themes.Active.DisplayName}（立绘 {art.LeftHeightRatio:P0}/{art.RightHeightRatio:P0}）";

        ResizeCharacters();
    }

    private void ResizeCharacters()
    {
        if (_themes.Art is not { } art || ContentHost.ActualHeight <= 0)
        {
            return;
        }

        CharacterLeftImage.Height = ContentHost.ActualHeight * art.LeftHeightRatio;
        CharacterRightImage.Height = ContentHost.ActualHeight * art.RightHeightRatio;
        CharacterLeftImage.MaxWidth = ContentHost.ActualWidth * 0.45;
        CharacterRightImage.MaxWidth = ContentHost.ActualWidth * 0.45;
    }
}
