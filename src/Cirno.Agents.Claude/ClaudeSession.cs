using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cirno.Agents.Abstractions;

namespace Cirno.Agents.Claude;

/// <summary>
/// Claude Code 的 stream-json 适配器：一个会话对应一个进程，进程即会话。
/// 协议解析在 <see cref="ClaudeEventParser"/>，本类只管进程、管道与控制通道。
/// </summary>
public sealed class ClaudeSession : IAgentSession, IClaudeStreamSink
{
    private readonly AgentSessionOptions _options;
    private readonly object _gate = new();
    private readonly Dictionary<string, TaskCompletionSource<JsonElement>> _pendingControls = [];
    private readonly StringBuilder _stderrTail = new();
    private readonly ClaudeEventParser _parser;

    private Process? _process;
    private StreamWriter? _stdin;
    private SemaphoreSlim? _writeLock;
    private CancellationTokenSource? _lifetime;
    private bool _disposed;

    public ClaudeSession(AgentSessionOptions options)
    {
        _options = options;
        SessionId = options.SessionId;
        WorkingDirectory = options.WorkingDirectory;
        _parser = new ClaudeEventParser(this);
        _parser.ControlResponseReceived += OnControlResponse;
    }

    public string SessionId { get; }

    public string WorkingDirectory { get; }

    public AgentSessionState State { get; private set; } = AgentSessionState.Starting;

    public event Action<AgentEvent>? EventReceived;

    // ---------- IClaudeStreamSink ----------

    void IClaudeStreamSink.Raise(AgentEvent evt) => Raise(evt);

    Task IClaudeStreamSink.WriteControlAsync(JsonObject payload, CancellationToken cancellationToken)
        => WriteLineAsync(payload, cancellationToken);

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_process is not null)
        {
            return Task.CompletedTask;
        }

        var located = ClaudeCliLocator.Locate()
            ?? throw new InvalidOperationException(
                "未找到 claude 可执行文件。请先安装 Claude Code（npm i -g @anthropic-ai/claude-code），或用 CIRNO_CLAUDE_PATH 指定路径。");

        var psi = new ProcessStartInfo
        {
            FileName = located.FileName,
            WorkingDirectory = _options.WorkingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        ClaudeCliLocator.ApplyArguments(psi, located, BuildArguments());

        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.Exited += OnProcessExited;

        if (!_process.Start())
        {
            throw new InvalidOperationException("claude 进程启动失败。");
        }

        // 交给宿主托管（Job Object）。放在启动后立刻执行，缩小孤儿进程窗口。
        _options.OnProcessStarted?.Invoke(_process.Id);

        _stdin = new StreamWriter(_process.StandardInput.BaseStream, new UTF8Encoding(false))
        {
            AutoFlush = true,
            NewLine = "\n",
        };
        _writeLock = new SemaphoreSlim(1, 1);
        _lifetime = new CancellationTokenSource();

        _ = Task.Run(() => ReadStdoutAsync(_lifetime.Token));
        _ = Task.Run(() => ReadStderrAsync(_lifetime.Token));

        State = AgentSessionState.Idle;
        Raise(new SessionStateChangedEvent(State));

        // 不等待应答：initialize 只是注册 host 的可用性，CLI 收到后会回 commands 列表
        _ = SendControlAsync("initialize", new JsonObject(), TimeSpan.FromSeconds(10));

        return Task.CompletedTask;
    }

    private IEnumerable<string> BuildArguments()
    {
        yield return "--print";
        yield return "--input-format";
        yield return "stream-json";
        yield return "--output-format";
        yield return "stream-json";
        yield return "--verbose";
        yield return "--include-partial-messages";

        // 审批走 stdio 回到宿主；没有这个开关时 CLI 会直接拒绝所有需要授权的工具
        yield return "--permission-prompt-tool";
        yield return "stdio";
        yield return "--permission-prompts";
        yield return "host";

        if (!string.IsNullOrWhiteSpace(_options.ResumeSessionId))
        {
            yield return "--resume";
            yield return _options.ResumeSessionId;
        }
        else
        {
            yield return "--session-id";
            yield return SessionId;
        }

        if (!string.IsNullOrWhiteSpace(_options.Model))
        {
            yield return "--model";
            yield return _options.Model;
        }

        if (!string.IsNullOrWhiteSpace(_options.PermissionMode))
        {
            yield return "--permission-mode";
            yield return _options.PermissionMode;
        }

        // 开发期/自动化通道：额外的 CLI 参数，主要用于强制走审批规则做回归
        var extra = Environment.GetEnvironmentVariable("CIRNO_CLAUDE_EXTRA_ARGS");
        if (!string.IsNullOrWhiteSpace(extra))
        {
            foreach (var arg in extra.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                yield return arg;
            }
        }
    }

    public async Task SendUserMessageAsync(string text, CancellationToken cancellationToken = default)
    {
        if (_stdin is null)
        {
            throw new InvalidOperationException("会话尚未启动。");
        }

        var itemId = $"user:{Guid.NewGuid():n}";
        var item = new AgentItem
        {
            Id = itemId,
            Kind = AgentItemKind.UserMessage,
            Text = text,
        };

        State = AgentSessionState.Running;
        Raise(new TurnStartedEvent(Guid.NewGuid().ToString("n"), null));
        Raise(new ItemStartedEvent(item));
        Raise(new ItemCompletedEvent(item));
        Raise(new SessionStateChangedEvent(State));

        await WriteLineAsync(new JsonObject
        {
            ["type"] = "user",
            ["message"] = new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray(new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = text,
                }),
            },
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task ResolveApprovalAsync(string requestId, ApprovalOutcome outcome, string? message = null)
    {
        if (!_parser.TryTakeApproval(requestId, out var pending) || pending is null)
        {
            return;
        }

        var response = new JsonObject();
        switch (outcome)
        {
            case ApprovalOutcome.Allow:
                response["behavior"] = "allow";
                // 原样回传输入：改写过输入等于伪造工具参数
                response["updatedInput"] = pending.Input.DeepClone();
                break;
            case ApprovalOutcome.Deny:
                response["behavior"] = "deny";
                response["message"] = message ?? "用户拒绝了该工具调用。";
                break;
            default:
                response["behavior"] = "deny";
                response["message"] = message ?? "已取消。";
                break;
        }

        await WriteLineAsync(new JsonObject
        {
            ["type"] = "control_response",
            ["response"] = new JsonObject
            {
                ["subtype"] = "success",
                ["request_id"] = requestId,
                ["response"] = response,
            },
        }, CancellationToken.None).ConfigureAwait(false);

        if (State == AgentSessionState.AwaitingApproval)
        {
            State = AgentSessionState.Running;
            Raise(new SessionStateChangedEvent(State));
        }

        Raise(new ApprovalResolvedEvent(requestId, outcome));
    }

    public async Task InterruptAsync(CancellationToken cancellationToken = default)
    {
        if (_stdin is null)
        {
            return;
        }

        var ok = await SendControlAsync("interrupt", new JsonObject(), TimeSpan.FromSeconds(5))
            .ConfigureAwait(false);
        if (!ok)
        {
            // 控制通道无应答时退化为进程级中断，避免界面卡在「运行中」
            Raise(new AgentFaultEvent(new AgentError
            {
                Code = "interrupt_no_ack",
                Message = "中断请求未得到后端确认，已结束当前进程。",
                IsRetryable = true,
            }));
            State = AgentSessionState.Interrupted;
            Raise(new SessionStateChangedEvent(State));
            KillProcessTree();
        }
    }

    // ---------- 输出读取 ----------

    private async Task ReadStdoutAsync(CancellationToken cancellationToken)
    {
        var stdout = _process?.StandardOutput;
        if (stdout is null)
        {
            return;
        }

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await stdout.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (line.Length == 0)
                {
                    continue;
                }

                try
                {
                    _parser.HandleLine(line);
                }
                catch (Exception ex)
                {
                    Raise(new AgentFaultEvent(new AgentError
                    {
                        Code = "parse_failed",
                        Message = $"无法解析后端输出：{ex.Message}",
                        IsRetryable = false,
                    }));
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常关闭
        }
        catch (Exception ex)
        {
            Raise(new AgentFaultEvent(new AgentError
            {
                Code = "stdout_failed",
                Message = ex.Message,
                IsRetryable = true,
            }));
        }
    }

    private async Task ReadStderrAsync(CancellationToken cancellationToken)
    {
        var stderr = _process?.StandardError;
        if (stderr is null)
        {
            return;
        }

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await stderr.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                lock (_gate)
                {
                    _stderrTail.AppendLine(line);
                    if (_stderrTail.Length > 8192)
                    {
                        _stderrTail.Remove(0, _stderrTail.Length - 8192);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // 标准错误是诊断通道，读失败不应影响主链路
        }
    }

    private void OnControlResponse(string requestId, JsonElement response)
    {
        TaskCompletionSource<JsonElement>? tcs;
        lock (_gate)
        {
            if (!_pendingControls.Remove(requestId, out tcs))
            {
                return;
            }
        }

        tcs.TrySetResult(response);
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        var code = _process?.ExitCode ?? -1;
        string stderr;
        lock (_gate)
        {
            stderr = _stderrTail.ToString();
        }

        State = AgentSessionState.Failed;
        Raise(new AgentFaultEvent(new AgentError
        {
            Code = "process_exited",
            Message = string.IsNullOrWhiteSpace(stderr)
                ? $"后端进程已退出（退出码 {code}）。"
                : $"后端进程已退出（退出码 {code}）：{stderr.Trim()}",
            IsRetryable = true,
        }));
        Raise(new SessionStateChangedEvent(State));
    }

    // ---------- 写入 ----------

    private async Task WriteLineAsync(JsonObject payload, CancellationToken cancellationToken)
    {
        if (_stdin is null || _writeLock is null)
        {
            return;
        }

        var line = payload.ToJsonString();
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stdin.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await _stdin.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Raise(new AgentFaultEvent(new AgentError
            {
                Code = "stdin_failed",
                Message = $"写入后端失败：{ex.Message}",
                IsRetryable = true,
            }));
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task<bool> SendControlAsync(string subtype, JsonObject request, TimeSpan timeout)
    {
        var requestId = $"ctrl:{Guid.NewGuid():n}";
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _pendingControls[requestId] = tcs;
        }

        var payload = new JsonObject
        {
            ["type"] = "control_request",
            ["request_id"] = requestId,
            ["request"] = MergeSubtype(subtype, request),
        };

        await WriteLineAsync(payload, CancellationToken.None).ConfigureAwait(false);

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeout)).ConfigureAwait(false);
        if (completed != tcs.Task)
        {
            lock (_gate)
            {
                _pendingControls.Remove(requestId);
            }

            return false;
        }

        return true;
    }

    private static JsonObject MergeSubtype(string subtype, JsonObject request)
    {
        var node = new JsonObject { ["subtype"] = subtype };
        foreach (var pair in request)
        {
            node[pair.Key] = pair.Value?.DeepClone();
        }

        return node;
    }

    // ---------- 辅助 ----------

    private void Raise(AgentEvent evt)
    {
        // 状态机是会话层的职责：解析器只产出事件，状态由这里推导。
        switch (evt)
        {
            case ApprovalRequestedEvent:
                State = AgentSessionState.AwaitingApproval;
                Raise(new SessionStateChangedEvent(State));
                break;

            case TurnCompletedEvent completed:
                // 被中断的轮次 stop_reason 为 null 且 is_error 为 true，
                // 不能因此判成失败态——否则界面会把「用户按了停止」显示成错误。
                State = completed.Result.StopReason is null
                    ? AgentSessionState.Interrupted
                    : completed.Result.IsError
                        ? AgentSessionState.Failed
                        : AgentSessionState.Idle;
                Raise(new SessionStateChangedEvent(State));
                break;
        }

        try
        {
            EventReceived?.Invoke(evt);
        }
        catch
        {
            // 订阅方异常不应影响读取循环
        }
    }

    // ---------- 释放 ----------

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // 会话销毁时统一回绝所有挂起的审批，否则工具调用会永久阻塞
        foreach (var id in _parser.PendingApprovalIds)
        {
            await ResolveApprovalAsync(id, ApprovalOutcome.Deny, "会话已关闭。").ConfigureAwait(false);
        }

        _lifetime?.Cancel();

        try
        {
            _stdin?.Close();
        }
        catch
        {
            // 进程可能已退出
        }

        if (IsProcessAlive())
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                await _process!.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                KillProcessTree();
            }
        }

        _process?.Dispose();
        _stdin?.Dispose();
        _writeLock?.Dispose();
        _lifetime?.Dispose();
    }

    private void KillProcessTree()
    {
        try
        {
            if (IsProcessAlive())
            {
                _process!.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // 已被回收
        }
    }

    /// <summary>进程是否还在跑。启动失败或已释放时统一视为「不在跑」。</summary>
    private bool IsProcessAlive()
    {
        if (_process is null)
        {
            return false;
        }

        try
        {
            return !_process.HasExited;
        }
        catch (InvalidOperationException)
        {
            // 进程对象尚未关联到系统进程（启动失败）
            return false;
        }
    }
}
