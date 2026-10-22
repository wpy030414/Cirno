using Cirno.Shell.Windows;
using Microsoft.UI.Xaml;

namespace Cirno.App;

public partial class App : Application
{
    public static Window? MainWindow { get; private set; }

    private SingleInstance? _instance;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // 标记本版本支持优雅关闭：安装器据此决定用不用管道方式关掉运行中的实例
        Cirno.App.Services.InstallMarker.MarkShutdownSupported();

        // 安装升级前的关闭指令：不属于正常启动路径，先于一切处理
        if (HasArgument("--shutdown"))
        {
            HandleShutdownCommand();
            return;
        }

        // 必须在创建任何窗口之前声明 AUMID：任务栏分组与跳转列表都以此为准
        JumpListService.RegisterAppUserModelId();

        _instance = new SingleInstance();
        if (!_instance.TryAcquire())
        {
            // 已有实例在跑：把启动意图转交过去，本次进程直接退出
            SingleInstance.ForwardToPrimary(ActivationRequest.FromCommandLine().Serialize());
            _instance.Dispose();
            _instance = null;
            Exit();
            return;
        }

        _instance.ActivationRequested += OnActivationRequested;
        _instance.ShutdownRequested += OnShutdownRequested;

        MainWindow = new MainWindow(_instance);
        MainWindow.Activate();
    }

    /// <summary>
    /// 响应安装器的关闭请求：请主实例退出，并等它真正走完再返回。
    /// 安装器据此确认文件已不被占用。
    /// </summary>
    private void HandleShutdownCommand()
    {
        SingleInstance.RequestShutdown();
        SingleInstance.WaitForPrimaryExit(TimeSpan.FromSeconds(20));
        Exit();
    }

    private void OnActivationRequested(string payload)
    {
        var request = ActivationRequest.Parse(payload);

        // 跨线程回到 UI 线程
        if (MainWindow is not MainWindow window)
        {
            return;
        }

        window.DispatcherQueue.TryEnqueue(() =>
        {
            window.Activate();
            window.HandleActivation(request);
        });
    }

    private void OnShutdownRequested()
    {
        if (MainWindow is not MainWindow window)
        {
            return;
        }

        window.DispatcherQueue.TryEnqueue(() => window.Close());
    }

    private static bool HasArgument(string name)
        => Environment.GetCommandLineArgs()
            .Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
}
