using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cirno.Agents.Abstractions;

namespace Cirno.Agents.Claude;

/// <summary>
/// Claude Code 的 stream-json 适配器：一个会话对应一个进程，进程即会话。
/// 协议事实（已在真实 CLI 上实测）：
/// <list type="bullet">
/// <item>审批需要 <c>--permission-prompt-tool stdio</c>，否则工具调用会被自动拒绝；</item>
/// <item>host 必须先发 <c>initialize</c> 控制请求，CLI 才会把 <c>can_use_tool</c> 路由过来；</item>
/// <item>每个审批请求必须有终态应答，否则工具调用永久阻塞且没有超时。</item>
/// </list>
/// </summary>
public sealed class ClaudeSession : IAgentSession
{
    private readonly AgentSessionOptions _options;
    private readonly object _gate = new();
    private readonly Dictionary<string, PendingApproval> _pendingApprovals = [];
    private readonly Dictionary<string, TaskCompletionSource<JsonElement>> _pendingControls = [];
    private readonly Dictionary<string, AgentItem> _items = [];
    private readonly StringBuilder _stderrTail = new();

    private Process? _process;
    private StreamWriter? _stdin;
    private SemaphoreSlim? _writeLock;
    private CancellationTokenSource? _lifetime;
    private string? _currentApiMessageId;
    private bool _disposed;

    public ClaudeSession(AgentSessionOptions options)
    {
        _options = options;
        SessionId = options.SessionId;
        WorkingDirectory = options.WorkingDirectory;
    }

    public string SessionId { get; }

    public string WorkingDirectory { get; }

    public AgentSessionState State { get; private set; } = AgentSessionState.Starting;

    public event Action<AgentEvent>? EventReceived;

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

        if (!string.IsNullOrEmpty(located.ArgumentPrefix))
        {
            psi.Arguments = located.ArgumentPrefix;
        }

        foreach (var arg in BuildArguments())
        {
            psi.ArgumentList.Add(arg);
        }

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
        Remember(item);

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
        PendingApproval? pending;
        lock (_gate)
        {
            if (!_pendingApprovals.Remove(requestId, out pending))
            {
                return;
            }
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

    // ---------- 输出解析 ----------

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
                    HandleLine(line);
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

    private void HandleLine(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        var type = GetString(root, "type");

        switch (type)
        {
            case "system":
                HandleSystem(root);
                break;
            case "assistant":
                HandleAssistant(root);
                break;
            case "user":
                HandleUser(root);
                break;
            case "stream_event":
                HandleStreamEvent(root);
                break;
            case "result":
                HandleResult(root);
                break;
            case "control_request":
                HandleControlRequest(root);
                break;
            case "control_response":
                HandleControlResponse(root);
                break;
        }
    }

    private void HandleSystem(JsonElement root)
    {
        var subtype = GetString(root, "subtype");
        switch (subtype)
        {
            case "init":
                _currentBackendSessionId = GetString(root, "session_id");
                Raise(new SessionInitEvent(new SessionInit
                {
                    BackendSessionId = _currentBackendSessionId ?? SessionId,
                    Model = GetString(root, "model"),
                    PermissionMode = GetString(root, "permissionMode"),
                    BackendVersion = GetString(root, "claude_code_version"),
                    Tools = ReadStringArray(root, "tools"),
                }));
                break;

            case "compact_boundary":
                RaiseItem(new AgentItem
                {
                    Id = $"notice:compact:{Guid.NewGuid():n}",
                    Kind = AgentItemKind.Notice,
                    Text = "上下文已压缩。",
                });
                break;
        }
    }

    private string? _currentBackendSessionId;

    private void HandleAssistant(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var message))
        {
            return;
        }

        var messageId = GetString(message, "id") ?? $"msg:{Guid.NewGuid():n}";
        _currentApiMessageId = messageId;

        if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var index = 0;
        foreach (var block in content.EnumerateArray())
        {
            var blockType = GetString(block, "type");
            switch (blockType)
            {
                case "text":
                {
                    var text = GetString(block, "text") ?? string.Empty;
                    CompleteOrRaise(new AgentItem
                    {
                        Id = $"blk:{messageId}:{index}",
                        Kind = AgentItemKind.AssistantMessage,
                        Text = text,
                        Raw = block.Clone(),
                    });
                    break;
                }

                case "thinking":
                {
                    var thinking = GetString(block, "thinking");
                    if (!string.IsNullOrWhiteSpace(thinking))
                    {
                        CompleteOrRaise(new AgentItem
                        {
                            Id = $"blk:{messageId}:{index}",
                            Kind = AgentItemKind.Reasoning,
                            Text = thinking,
                            Raw = block.Clone(),
                        });
                    }

                    break;
                }

                case "tool_use":
                {
                    var toolUseId = GetString(block, "id") ?? $"tool:{Guid.NewGuid():n}";
                    var name = GetString(block, "name") ?? "tool";
                    var input = block.TryGetProperty("input", out var i) ? i.Clone() : (JsonElement?)null;
                    var item = BuildToolItem(toolUseId, name, input);
                    if (!_items.ContainsKey(item.Id))
                    {
                        RaiseItem(item);
                    }

                    break;
                }
            }

            index++;
        }
    }

    private void HandleUser(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var message) ||
            !message.TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var block in content.EnumerateArray())
        {
            if (GetString(block, "type") != "tool_result")
            {
                continue;
            }

            var toolUseId = GetString(block, "tool_use_id");
            if (toolUseId is null)
            {
                continue;
            }

            var isError = block.TryGetProperty("is_error", out var err) && err.ValueKind == JsonValueKind.True;
            var resultText = ReadContentText(block);

            AgentItem baseItem;
            lock (_gate)
            {
                if (!_items.TryGetValue(toolUseId, out var existing))
                {
                    existing = new AgentItem
                    {
                        Id = toolUseId,
                        Kind = AgentItemKind.ToolCall,
                        ToolUseId = toolUseId,
                        Title = "工具",
                    };
                }

                baseItem = existing;
            }

            // 文件变更的 Detail 就是 diff 本体，成功回执（"updated successfully"）
            // 混进去会变成一堆无意义的上下文行；只在出错时保留错误信息。
            var keepResult = baseItem.Kind != AgentItemKind.FileChange || isError;
            var detail = !keepResult || string.IsNullOrWhiteSpace(resultText)
                ? baseItem.Detail
                : string.IsNullOrWhiteSpace(baseItem.Detail)
                    ? resultText
                    : baseItem.Detail + "\n\n" + resultText;

            var completed = baseItem with
            {
                Detail = detail,
                IsError = isError,
                IsStreaming = false,
                Timestamp = DateTimeOffset.Now,
            };
            Remember(completed);
            Raise(new ItemCompletedEvent(completed));
        }
    }

    private void HandleStreamEvent(JsonElement root)
    {
        if (!root.TryGetProperty("event", out var ev) || ev.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var evType = GetString(ev, "type");
        switch (evType)
        {
            case "message_start":
                if (ev.TryGetProperty("message", out var startMessage))
                {
                    _currentApiMessageId = GetString(startMessage, "id") ?? _currentApiMessageId;
                }

                break;

            case "content_block_start":
            {
                var index = GetInt(ev, "index") ?? 0;
                if (ev.TryGetProperty("content_block", out var cb) && GetString(cb, "type") == "text")
                {
                    var id = $"blk:{_currentApiMessageId ?? "live"}:{index}";
                    var item = new AgentItem
                    {
                        Id = id,
                        Kind = AgentItemKind.AssistantMessage,
                        Text = GetString(cb, "text") ?? string.Empty,
                        IsStreaming = true,
                    };
                    Remember(item);
                    Raise(new ItemStartedEvent(item));
                }

                break;
            }

            case "content_block_delta":
            {
                var index = GetInt(ev, "index") ?? 0;
                if (!ev.TryGetProperty("delta", out var delta) ||
                    GetString(delta, "type") != "text_delta")
                {
                    break;
                }

                var text = GetString(delta, "text");
                if (!string.IsNullOrEmpty(text))
                {
                    Raise(new ItemDeltaEvent($"blk:{_currentApiMessageId ?? "live"}:{index}", text));
                }

                break;
            }
        }
    }

    private void HandleResult(JsonElement root)
    {
        _currentApiMessageId = null;

        var usage = root.TryGetProperty("usage", out var u) ? u : default;
        var result = new TurnResult
        {
            IsError = root.TryGetProperty("is_error", out var ie) && ie.ValueKind == JsonValueKind.True,
            StopReason = GetString(root, "stop_reason"),
            InputTokens = GetLong(usage, "input_tokens"),
            OutputTokens = GetLong(usage, "output_tokens"),
            CostUsd = root.TryGetProperty("total_cost_usd", out var c) && c.TryGetDecimal(out var cost) ? cost : 0m,
            Duration = TimeSpan.FromMilliseconds(GetLong(root, "duration_ms")),
        };

        // 被中断的轮次 stop_reason 为 null 且 is_error 为 true，
        // 不能因此判成失败态——否则界面会把「用户按了停止」显示成错误。
        State = result.StopReason is null
            ? AgentSessionState.Interrupted
            : result.IsError
                ? AgentSessionState.Failed
                : AgentSessionState.Idle;

        Raise(new TurnCompletedEvent(result));
        Raise(new SessionStateChangedEvent(State));
    }

    private void HandleControlRequest(JsonElement root)
    {
        var requestId = GetString(root, "request_id");
        if (requestId is null || !root.TryGetProperty("request", out var request))
        {
            return;
        }

        var subtype = GetString(request, "subtype");
        if (subtype != "can_use_tool")
        {
            // 未注册的宿主能力（hooks 等）：明确回绝，避免后端等待
            _ = WriteLineAsync(new JsonObject
            {
                ["type"] = "control_response",
                ["response"] = new JsonObject
                {
                    ["subtype"] = "error",
                    ["request_id"] = requestId,
                    ["error"] = "unsupported_control_request",
                },
            }, CancellationToken.None);
            return;
        }

        var toolName = GetString(request, "tool_name") ?? "tool";
        var input = request.TryGetProperty("input", out var i) ? i.Clone() : (JsonElement?)null;
        var toolUseId = GetString(request, "tool_use_id");

        var detail = DescribeToolCall(toolName, input);
        var description = GetString(request, "description");

        lock (_gate)
        {
            _pendingApprovals[requestId] = new PendingApproval
            {
                RequestId = requestId,
                ToolUseId = toolUseId,
                Input = input is null ? new JsonObject() : JsonNode.Parse(input.Value.GetRawText())!.AsObject(),
            };
        }

        State = AgentSessionState.AwaitingApproval;
        Raise(new SessionStateChangedEvent(State));
        Raise(new ApprovalRequestedEvent(new ApprovalRequest
        {
            RequestId = requestId,
            ToolName = toolName,
            ToolUseId = toolUseId,
            Title = GetString(request, "display_name") ?? toolName,
            Detail = string.IsNullOrWhiteSpace(description) ? detail : description,
            Reason = GetString(request, "decision_reason_type"),
            Input = input,
        }));
    }

    private void HandleControlResponse(JsonElement root)
    {
        if (!root.TryGetProperty("response", out var response))
        {
            return;
        }

        var requestId = GetString(response, "request_id");
        if (requestId is null)
        {
            return;
        }

        TaskCompletionSource<JsonElement>? tcs;
        lock (_gate)
        {
            if (!_pendingControls.Remove(requestId, out tcs))
            {
                return;
            }
        }

        tcs.TrySetResult(response.Clone());
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

    // ---------- 工具映射 ----------

    private static AgentItem BuildToolItem(string toolUseId, string name, JsonElement? input)
    {
        var kind = name switch
        {
            "Bash" or "BashOutput" or "KillShell" => AgentItemKind.CommandExecution,
            "Edit" or "Write" or "MultiEdit" or "NotebookEdit" => AgentItemKind.FileChange,
            "Task" or "TodoWrite" or "ExitPlanMode" => AgentItemKind.Plan,
            _ => AgentItemKind.ToolCall,
        };

        var detail = kind == AgentItemKind.FileChange
            ? DiffBuilder.DescribeFileChange(name, input)
            : DescribeToolCall(name, input);

        return new AgentItem
        {
            Id = toolUseId,
            Kind = kind,
            Title = name,
            ToolName = name,
            ToolUseId = toolUseId,
            Detail = detail,
            IsStreaming = true,
        };
    }

    private static string? DescribeToolCall(string toolName, JsonElement? input)
    {
        if (input is not { } i || i.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return toolName switch
        {
            "Bash" => GetString(i, "command"),
            "Read" or "Write" or "Edit" or "MultiEdit" => GetString(i, "file_path"),
            "Glob" => GetString(i, "pattern"),
            "Grep" => GetString(i, "pattern"),
            "WebFetch" => GetString(i, "url"),
            "WebSearch" => GetString(i, "query"),
            "Task" => GetString(i, "description") ?? GetString(i, "prompt"),
            _ => null,
        };
    }

    // ---------- 辅助 ----------

    private void Raise(AgentEvent evt)
    {
        try
        {
            EventReceived?.Invoke(evt);
        }
        catch
        {
            // 订阅方异常不应影响读取循环
        }
    }

    private void RaiseItem(AgentItem item)
    {
        Remember(item);
        Raise(new ItemStartedEvent(item));
        if (!item.IsStreaming)
        {
            Raise(new ItemCompletedEvent(item));
        }
    }

    private void CompleteOrRaise(AgentItem item)
    {
        bool existed;
        lock (_gate)
        {
            existed = _items.ContainsKey(item.Id);
        }

        var final = item with { IsStreaming = false };
        Remember(final);

        if (!existed)
        {
            Raise(new ItemStartedEvent(final));
        }

        Raise(new ItemCompletedEvent(final));
    }

    private void Remember(AgentItem item)
    {
        lock (_gate)
        {
            _items[item.Id] = item;
        }
    }

    private static string? GetString(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object &&
           element.TryGetProperty(name, out var value) &&
           value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? GetInt(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object &&
           element.TryGetProperty(name, out var value) &&
           value.ValueKind == JsonValueKind.Number &&
           value.TryGetInt32(out var n)
            ? n
            : null;

    private static long GetLong(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object &&
           element.TryGetProperty(name, out var value) &&
           value.ValueKind == JsonValueKind.Number &&
           value.TryGetInt64(out var n)
            ? n
            : 0;

    private static IReadOnlyList<string> ReadStringArray(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var list = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { } s)
            {
                list.Add(s);
            }
        }

        return list;
    }

    /// <summary>工具结果可能是字符串，也可能是内容块数组。</summary>
    private static string ReadContentText(JsonElement block)
    {
        if (!block.TryGetProperty("content", out var content))
        {
            return string.Empty;
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString() ?? string.Empty;
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        foreach (var part in content.EnumerateArray())
        {
            if (part.ValueKind == JsonValueKind.String)
            {
                sb.AppendLine(part.GetString());
                continue;
            }

            if (GetString(part, "type") == "text" && GetString(part, "text") is { } text)
            {
                sb.AppendLine(text);
            }
            else if (GetString(part, "type") == "image")
            {
                sb.AppendLine("[图片]");
            }
        }

        return sb.ToString().TrimEnd();
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
        List<string> pendingIds;
        lock (_gate)
        {
            pendingIds = [.. _pendingApprovals.Keys];
        }

        foreach (var id in pendingIds)
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

        if (_process is { HasExited: false })
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
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
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // 已被回收
        }
    }
}

internal sealed class PendingApproval
{
    public required string RequestId { get; init; }
    public string? ToolUseId { get; init; }
    public required JsonObject Input { get; init; }
}
