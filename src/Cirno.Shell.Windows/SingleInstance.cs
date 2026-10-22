using System.IO.Pipes;
using System.Text;

namespace Cirno.Shell.Windows;

/// <summary>
/// 单实例协调器。
/// <para>
/// Windows 应用通常只允许一个实例：第二次启动不应该再开一个窗口，
/// 而应该把「意图」交给已有实例（如从跳转列表点「新建会话」）。
/// </para>
/// <para>
/// 实现：命名互斥体判主从 + 命名管道传参。主实例监听管道，从实例把命令写过去。
/// 管道协议：<c>&lt;动词&gt;\u001f&lt;参数&gt;</c>，动词取 <see cref="VerbActivate"/> / <see cref="VerbShutdown"/>。
/// </para>
/// </summary>
public sealed class SingleInstance : IDisposable
{
    /// <summary>动词：激活/新建会话等。</summary>
    public const string VerbActivate = "activate";

    /// <summary>动词：关闭主实例（安装升级前用）。</summary>
    public const string VerbShutdown = "shutdown";

    private const string MutexName = @"Local\Cirno.SingleInstance";
    private const string PipeName = "Cirno.SingleInstance.Pipe";

    private readonly Mutex _mutex;
    private CancellationTokenSource? _listenerCts;
    private Task? _listenerTask;
    private bool _isPrimary;
    private bool _disposed;

    public SingleInstance()
    {
        _mutex = new Mutex(initiallyOwned: false, MutexName);
    }

    /// <summary>已有实例收到来自新实例的启动参数。</summary>
    public event Action<string>? ActivationRequested;

    /// <summary>已有实例收到关闭请求（安装器在覆盖文件前调用）。</summary>
    public event Action? ShutdownRequested;

    /// <summary>当前进程是否为主实例。</summary>
    public bool IsPrimary => _isPrimary;

    /// <summary>
    /// 尝试成为主实例。
    /// 返回 false 表示已有实例在运行，此时应把参数转发过去并退出。
    /// </summary>
    public bool TryAcquire()
    {
        try
        {
            _isPrimary = _mutex.WaitOne(TimeSpan.Zero, exitContext: false);
        }
        catch (AbandonedMutexException)
        {
            // 上一个持有者异常退出：互斥体已归我们所有
            _isPrimary = true;
        }

        if (_isPrimary)
        {
            StartListener();
        }

        return _isPrimary;
    }

    /// <summary>把启动参数转发给主实例。返回是否成功送达。</summary>
    public static bool ForwardToPrimary(string arguments)
        => Send(VerbActivate, arguments);

    /// <summary>请求主实例退出。返回是否成功送达。</summary>
    public static bool RequestShutdown()
        => Send(VerbShutdown, string.Empty);

    /// <summary>
    /// 等待主实例真正退出（互斥体可被独占）。
    /// 安装器需要在覆盖文件之前确认进程已走，否则文件仍被占用。
    /// </summary>
    public static bool WaitForPrimaryExit(TimeSpan timeout)
    {
        try
        {
            using var mutex = new Mutex(initiallyOwned: false, MutexName);
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    if (mutex.WaitOne(TimeSpan.Zero, exitContext: false))
                    {
                        mutex.ReleaseMutex();
                        return true;
                    }
                }
                catch (AbandonedMutexException)
                {
                    return true;
                }

                Thread.Sleep(100);
            }

            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool Send(string verb, string payload)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(2000);

            var message = verb + '\u001f' + payload;
            var bytes = Encoding.UTF8.GetBytes(message + "\n");
            client.Write(bytes, 0, bytes.Length);
            client.Flush();
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void StartListener()
    {
        _listenerCts = new CancellationTokenSource();
        _listenerTask = Task.Run(() => ListenAsync(_listenerCts.Token));
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                using var reader = new StreamReader(server, Encoding.UTF8);
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                Dispatch(line);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
                // 管道被占用或断开：稍后重试
                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private void Dispatch(string line)
    {
        var separator = line.IndexOf('\u001f');
        var verb = separator < 0 ? line : line[..separator];
        var payload = separator < 0 ? string.Empty : line[(separator + 1)..];

        if (verb == VerbShutdown)
        {
            ShutdownRequested?.Invoke();
            return;
        }

        ActivationRequested?.Invoke(payload);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            _listenerCts?.Cancel();
        }
        catch
        {
            // 取消失败不影响退出
        }

        try
        {
            _listenerTask?.Wait(TimeSpan.FromSeconds(1));
        }
        catch
        {
            // 监听任务未在时限内结束
        }

        _listenerCts?.Dispose();

        if (_isPrimary)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // 未持有互斥体
            }
        }

        _mutex.Dispose();
    }
}
