using Cirno.Agents.Abstractions;
using Xunit;

namespace Cirno.Agents.Claude.Tests;

/// <summary>
/// 协议夹具回放：把真实 CLI 抓下来的 stream-json 逐行喂给解析器，断言归一化事件。
/// 这些夹具是「协议形状」的唯一事实来源——改适配器前先跑它们。
/// </summary>
public class ClaudeProtocolFixtureTests
{
    private static ClaudeEventParser ParseFixture(string name, RecordingSink sink)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", name);
        Assert.True(File.Exists(path), $"缺少夹具：{name}");

        var parser = new ClaudeEventParser(sink);
        foreach (var line in File.ReadAllLines(path))
        {
            if (line.Trim().Length == 0)
            {
                continue;
            }

            parser.HandleLine(line);
        }

        return parser;
    }

    [Fact]
    public void 文本轮次_产出初始化与完成事件()
    {
        var sink = new RecordingSink();
        ParseFixture("text-turn.jsonl", sink);

        var init = Assert.Single(sink.EventsOfType<SessionInitEvent>());
        Assert.Equal("11111111-2222-3333-4444-555555555555", init.Info.BackendSessionId);
        Assert.Equal("claude-sonnet-4-5", init.Info.Model);
        Assert.Contains("Bash", init.Info.Tools);

        var turn = Assert.Single(sink.EventsOfType<TurnCompletedEvent>());
        Assert.False(turn.Result.IsError);
        Assert.Equal("end_turn", turn.Result.StopReason);
        Assert.Equal(120, turn.Result.InputTokens);
        Assert.Equal(0.0021m, turn.Result.CostUsd);
    }

    [Fact]
    public void 流式增量_合并为同一条目且最终以完成事件收口()
    {
        var sink = new RecordingSink();
        ParseFixture("tool-call.jsonl", sink);

        var deltas = sink.EventsOfType<ItemDeltaEvent>().ToList();
        Assert.Equal(2, deltas.Count);
        Assert.Equal("我先看一下", deltas[0].Delta);
        Assert.All(deltas, d => Assert.Equal("blk:msg_02:0", d.ItemId));

        var completions = sink.EventsOfType<ItemCompletedEvent>()
            .Where(c => c.Item.Kind == AgentItemKind.AssistantMessage)
            .ToList();
        var assistant = Assert.Single(completions);
        Assert.Equal("我先看一下这个文件。", assistant.Item.Text);
        Assert.False(assistant.Item.IsStreaming);
    }

    [Fact]
    public void 工具调用_映射为条目并在结果到达后补全()
    {
        var sink = new RecordingSink();
        ParseFixture("tool-call.jsonl", sink);

        var tool = sink.EventsOfType<ItemCompletedEvent>()
            .Single(c => c.Item.Kind == AgentItemKind.ToolCall);
        Assert.Equal("Read", tool.Item.ToolName);
        Assert.Equal("toolu_01", tool.Item.ToolUseId);
        Assert.StartsWith("C:\\work\\a.txt", tool.Item.Detail);
        Assert.Contains("hello from file", tool.Item.Detail);
    }

    [Fact]
    public void 审批请求_产生审批事件并保留原始输入()
    {
        var sink = new RecordingSink();
        var parser = ParseFixture("approval-allow.jsonl", sink);

        var approval = Assert.Single(sink.EventsOfType<ApprovalRequestedEvent>());
        Assert.Equal("req_abc", approval.Request.RequestId);
        Assert.Equal("Bash", approval.Request.ToolName);
        Assert.Equal("运行 git status", approval.Request.Detail);

        // 输入原样保留：允许时应原样回传，改写等于伪造工具参数
        Assert.True(parser.TryTakeApproval("req_abc", out var pending));
        Assert.Equal("git status", pending!.Input["command"]!.GetValue<string>());

        // 同一个请求不能被应答两次
        Assert.False(parser.TryTakeApproval("req_abc", out _));
    }

    [Fact]
    public void 文件变更_成功回执不混入diff正文()
    {
        var sink = new RecordingSink();
        ParseFixture("file-change.jsonl", sink);

        var change = sink.EventsOfType<ItemCompletedEvent>()
            .Single(c => c.Item.Kind == AgentItemKind.FileChange);
        Assert.Equal("Edit", change.Item.ToolName);
        Assert.DoesNotContain("updated successfully", change.Item.Detail ?? string.Empty);
    }

    [Fact]
    public void 拒绝的工具调用_保留错误标记()
    {
        var sink = new RecordingSink();
        ParseFixture("approval-deny.jsonl", sink);

        var result = sink.EventsOfType<ItemCompletedEvent>()
            .Single(c => c.Item.Kind == AgentItemKind.CommandExecution);
        Assert.True(result.Item.IsError);
        Assert.Contains("用户拒绝", result.Item.Detail ?? string.Empty);
    }

    [Fact]
    public void 中断轮次_结果里stop_reason为空()
    {
        var sink = new RecordingSink();
        ParseFixture("interrupted.jsonl", sink);

        var turn = Assert.Single(sink.EventsOfType<TurnCompletedEvent>());
        Assert.True(turn.Result.IsError);
        Assert.Null(turn.Result.StopReason);
    }
}
