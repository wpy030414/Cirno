using System.Text.Json;
using System.Text.Json.Nodes;
using Cirno.Agents.Abstractions;

namespace Cirno.Agents.Claude.Tests;

/// <summary>测试用的事件收集器：记录解析器产出的一切，并可回放控制通道写入。</summary>
public sealed class RecordingSink : IClaudeStreamSink
{
    private readonly List<AgentEvent> _events = [];
    private readonly List<JsonObject> _controlWrites = [];
    private readonly object _gate = new();

    public IReadOnlyList<AgentEvent> Events
    {
        get
        {
            lock (_gate)
            {
                return [.. _events];
            }
        }
    }

    public IReadOnlyList<JsonObject> ControlWrites
    {
        get
        {
            lock (_gate)
            {
                return [.. _controlWrites];
            }
        }
    }

    public void Raise(AgentEvent evt)
    {
        lock (_gate)
        {
            _events.Add(evt);
        }
    }

    public Task WriteControlAsync(JsonObject payload, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _controlWrites.Add(payload);
        }

        return Task.CompletedTask;
    }

    public IEnumerable<T> EventsOfType<T>() where T : AgentEvent => Events.OfType<T>();

    public JsonObject? LastControlWrite() => ControlWrites.LastOrDefault();

    public static JsonObject? ParseObject(string json) => JsonNode.Parse(json)?.AsObject();
}
