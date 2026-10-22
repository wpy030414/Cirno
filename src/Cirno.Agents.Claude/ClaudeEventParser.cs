using System.Text.Json;
using System.Text.Json.Nodes;
using Cirno.Agents.Abstractions;

namespace Cirno.Agents.Claude;

/// <summary>解析器对外的出口：事件上报与控制通道写入。</summary>
public interface IClaudeStreamSink
{
    /// <summary>上报一个归一化事件。</summary>
    void Raise(AgentEvent evt);

    /// <summary>写回一条控制应答（例如对不支持的控制请求明确回绝）。</summary>
    Task WriteControlAsync(JsonObject payload, CancellationToken cancellationToken = default);
}

/// <summary>一次待应答的审批。</summary>
public sealed class ClaudePendingApproval
{
    public required string RequestId { get; init; }
    public string? ToolUseId { get; init; }
    public required JsonObject Input { get; init; }
}

/// <summary>
/// Claude Code stream-json 协议解析器。
/// <para>
/// 抽出来的目的：协议解析是纯状态机（输入一行 JSON → 产出归一化事件），
/// 不该被绑在进程与管道上，否则无法用抓包夹具做回归。
/// </para>
/// <para>
/// 协议事实（已在真实 CLI 上实测）：
/// <list type="bullet">
/// <item>审批需要 <c>--permission-prompt-tool stdio</c>，否则工具调用会被自动拒绝；</item>
/// <item>host 必须先发 <c>initialize</c> 控制请求，CLI 才会把 <c>can_use_tool</c> 路由过来；</item>
/// <item>每个审批请求必须有终态应答，否则工具调用永久阻塞且没有超时。</item>
/// </list>
/// </para>
/// </summary>
public sealed class ClaudeEventParser
{
    private readonly IClaudeStreamSink _sink;
    private readonly object _gate = new();
    private readonly Dictionary<string, AgentItem> _items = [];
    private readonly Dictionary<string, ClaudePendingApproval> _pendingApprovals = [];

    private string? _currentApiMessageId;
    private string? _currentBackendSessionId;

    public ClaudeEventParser(IClaudeStreamSink sink)
    {
        _sink = sink;
    }

    /// <summary>后端会话 id（来自 system/init）。</summary>
    public string? BackendSessionId => _currentBackendSessionId;

    /// <summary>收到后端对某次控制请求的应答（request_id → 原始响应）。</summary>
    public event Action<string, JsonElement>? ControlResponseReceived;

    /// <summary>当前等待应答的审批请求 id。</summary>
    public IReadOnlyList<string> PendingApprovalIds
    {
        get
        {
            lock (_gate)
            {
                return [.. _pendingApprovals.Keys];
            }
        }
    }

    /// <summary>处理一行 stdout。</summary>
    public void HandleLine(string line)
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

    /// <summary>取走一次待应答的审批（取走即终结，防止重复应答）。</summary>
    public bool TryTakeApproval(string requestId, out ClaudePendingApproval? approval)
    {
        lock (_gate)
        {
            if (_pendingApprovals.Remove(requestId, out var found))
            {
                approval = found;
                return true;
            }
        }

        approval = null;
        return false;
    }

    // ---------- 分派 ----------

    private void HandleSystem(JsonElement root)
    {
        switch (GetString(root, "subtype"))
        {
            case "init":
                _currentBackendSessionId = GetString(root, "session_id");
                _sink.Raise(new SessionInitEvent(new SessionInit
                {
                    BackendSessionId = _currentBackendSessionId ?? string.Empty,
                    Model = GetString(root, "model"),
                    PermissionMode = GetString(root, "permissionMode"),
                    BackendVersion = GetString(root, "claude_code_version"),
                    Tools = ReadStringArray(root, "tools"),
                }));
                break;

            case "compact_boundary":
            {
                var notice = new AgentItem
                {
                    Id = $"notice:compact:{Guid.NewGuid():n}",
                    Kind = AgentItemKind.Notice,
                    Text = "上下文已压缩。",
                };
                _sink.Raise(new ItemStartedEvent(notice));
                _sink.Raise(new ItemCompletedEvent(notice));
                break;
            }
        }
    }

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
            switch (GetString(block, "type"))
            {
                case "text":
                    CompleteOrRaise(new AgentItem
                    {
                        Id = $"blk:{messageId}:{index}",
                        Kind = AgentItemKind.AssistantMessage,
                        Text = GetString(block, "text") ?? string.Empty,
                        Raw = block.Clone(),
                    });
                    break;

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
                    bool known;
                    lock (_gate)
                    {
                        known = _items.ContainsKey(item.Id);
                    }

                    if (!known)
                    {
                        Remember(item);
                        _sink.Raise(new ItemStartedEvent(item));
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
            _sink.Raise(new ItemCompletedEvent(completed));
        }
    }

    private void HandleStreamEvent(JsonElement root)
    {
        if (!root.TryGetProperty("event", out var ev) || ev.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        switch (GetString(ev, "type"))
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
                    var item = new AgentItem
                    {
                        Id = $"blk:{_currentApiMessageId ?? "live"}:{index}",
                        Kind = AgentItemKind.AssistantMessage,
                        Text = GetString(cb, "text") ?? string.Empty,
                        IsStreaming = true,
                    };
                    Remember(item);
                    _sink.Raise(new ItemStartedEvent(item));
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
                    _sink.Raise(new ItemDeltaEvent($"blk:{_currentApiMessageId ?? "live"}:{index}", text));
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
            Duration = TimeSpan.FromMilliseconds(GetLong(usage, "duration_ms")),
        };

        _sink.Raise(new TurnCompletedEvent(result));
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
            _ = _sink.WriteControlAsync(new JsonObject
            {
                ["type"] = "control_response",
                ["response"] = new JsonObject
                {
                    ["subtype"] = "error",
                    ["request_id"] = requestId,
                    ["error"] = "unsupported_control_request",
                },
            });
            return;
        }

        var toolName = GetString(request, "tool_name") ?? "tool";
        var input = request.TryGetProperty("input", out var i) ? i.Clone() : (JsonElement?)null;
        var toolUseId = GetString(request, "tool_use_id");
        var description = GetString(request, "description");

        var detail = DescribeToolCall(toolName, input);

        lock (_gate)
        {
            _pendingApprovals[requestId] = new ClaudePendingApproval
            {
                RequestId = requestId,
                ToolUseId = toolUseId,
                Input = input is null ? new JsonObject() : JsonNode.Parse(input.Value.GetRawText())!.AsObject(),
            };
        }

        _sink.Raise(new ApprovalRequestedEvent(new ApprovalRequest
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
        // 控制应答由 ClaudeSession 自行关联（它持有 request_id → TCS 的映射）；
        // 解析器只把 request_id 透出去，避免两边重复维护状态。
        if (!root.TryGetProperty("response", out var response))
        {
            return;
        }

        var requestId = GetString(response, "request_id");
        if (requestId is not null)
        {
            ControlResponseReceived?.Invoke(requestId, response.Clone());
        }
    }

    // ---------- 工具映射 ----------

    internal static AgentItem BuildToolItem(string toolUseId, string name, JsonElement? input)
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

    internal static string? DescribeToolCall(string toolName, JsonElement? input)
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
            _sink.Raise(new ItemStartedEvent(final));
        }

        _sink.Raise(new ItemCompletedEvent(final));
    }

    private void Remember(AgentItem item)
    {
        lock (_gate)
        {
            _items[item.Id] = item;
        }
    }

    internal static string? GetString(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object &&
           element.TryGetProperty(name, out var value) &&
           value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    internal static int? GetInt(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object &&
           element.TryGetProperty(name, out var value) &&
           value.ValueKind == JsonValueKind.Number &&
           value.TryGetInt32(out var n)
            ? n
            : null;

    internal static long GetLong(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object &&
           element.TryGetProperty(name, out var value) &&
           value.ValueKind == JsonValueKind.Number &&
           value.TryGetInt64(out var n)
            ? n
            : 0;

    internal static IReadOnlyList<string> ReadStringArray(JsonElement element, string name)
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
    internal static string ReadContentText(JsonElement block)
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

        var sb = new System.Text.StringBuilder();
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
}
