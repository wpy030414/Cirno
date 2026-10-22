using System.Text;
using Cirno.Agents.Abstractions;
using Xunit;

namespace Cirno.Agents.Claude.Tests;

/// <summary>
/// 端到端：把假 CLI 当作真实后端拉起，验证进程、管道、控制通道、审批往返。
/// 不依赖真实 claude，也不需要网络。
/// </summary>
public class ClaudeSessionEndToEndTests : IDisposable
{
    private readonly string _scriptPath;
    private readonly string _wrapperPath;
    private readonly string _tempRoot;

    public ClaudeSessionEndToEndTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "cirno-fake-cli-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_tempRoot);

        // 假 CLI 是一个 dotnet 程序；用 .cmd 包一层，好让 ClaudeCliLocator 的脚本分支派上用场。
        var fakeDir = Path.Combine(AppContext.BaseDirectory, "fake-claude");
        var dll = Path.Combine(fakeDir, "Cirno.FakeClaude.dll");
        Assert.True(File.Exists(dll), $"缺少假 CLI：{dll}");

        _wrapperPath = Path.Combine(_tempRoot, "claude.cmd");
        File.WriteAllText(_wrapperPath, $"@echo off\r\n\"{DotnetPath()}\" \"{dll}\" %*\r\n", Encoding.UTF8);

        _scriptPath = Path.Combine(_tempRoot, "script.jsonl");
    }

    private static string DotnetPath()
    {
        var fromEnv = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv))
        {
            return fromEnv;
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir.Trim('"'), "dotnet.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("找不到 dotnet.exe");
    }

    private void UseScript(string fixtureName)
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "fixtures", fixtureName);
        Assert.True(File.Exists(fixture), $"缺少夹具：{fixtureName}");
        File.Copy(fixture, _scriptPath, overwrite: true);

        Environment.SetEnvironmentVariable("CIRNO_CLAUDE_PATH", _wrapperPath);
        Environment.SetEnvironmentVariable("CIRNO_FAKE_CLAUDE_SCRIPT", _scriptPath);
    }

    private static ClaudeSession CreateSession()
        => new(new AgentSessionOptions
        {
            SessionId = Guid.NewGuid().ToString(),
            WorkingDirectory = AppContext.BaseDirectory,
        });

    [Fact]
    public async Task 完整轮次_事件序列与状态收敛()
    {
        UseScript("text-turn.jsonl");
        var session = CreateSession();
        var events = new List<AgentEvent>();
        session.EventReceived += events.Add;

        await using (session)
        {
            await session.StartAsync();
            await session.SendUserMessageAsync("你好");
            await WaitUntil(() => events.OfType<TurnCompletedEvent>().Any(), TimeSpan.FromSeconds(20));
        }

        Assert.Contains(events, e => e is SessionInitEvent);
        Assert.Contains(events, e => e is TurnStartedEvent);
        Assert.Contains(events, e => e is ItemCompletedEvent { Item.Kind: AgentItemKind.AssistantMessage });

        var stateChanges = events.OfType<SessionStateChangedEvent>().Select(e => e.State).ToList();
        Assert.Contains(AgentSessionState.Running, stateChanges);
        Assert.Contains(AgentSessionState.Idle, stateChanges);
    }

    [Fact]
    public async Task 审批请求_允许后写回原始输入()
    {
        UseScript("approval-allow.jsonl");
        var session = CreateSession();
        var events = new List<AgentEvent>();
        session.EventReceived += events.Add;

        await using (session)
        {
            await session.StartAsync();
            await session.SendUserMessageAsync("跑一下");
            await WaitUntil(() => events.OfType<ApprovalRequestedEvent>().Any(), TimeSpan.FromSeconds(20));

            var approval = events.OfType<ApprovalRequestedEvent>().First();
            await session.ResolveApprovalAsync(approval.Request.RequestId, ApprovalOutcome.Allow);
        }

        Assert.Contains(events, e => e is ApprovalResolvedEvent { Outcome: ApprovalOutcome.Allow });
    }

    [Fact]
    public async Task 审批请求_拒绝后写回拒绝语义()
    {
        UseScript("approval-deny.jsonl");
        var session = CreateSession();
        var events = new List<AgentEvent>();
        session.EventReceived += events.Add;

        await using (session)
        {
            await session.StartAsync();
            await session.SendUserMessageAsync("跑一下");
            await WaitUntil(() => events.OfType<ApprovalRequestedEvent>().Any(), TimeSpan.FromSeconds(20));

            var approval = events.OfType<ApprovalRequestedEvent>().First();
            await session.ResolveApprovalAsync(approval.Request.RequestId, ApprovalOutcome.Deny, "用户拒绝了该工具调用。");
        }

        Assert.Contains(events, e => e is ApprovalResolvedEvent { Outcome: ApprovalOutcome.Deny });
    }

    [Fact]
    public async Task 中断_后端确认后不产生失败事件()
    {
        UseScript("interrupted.jsonl");
        var session = CreateSession();
        var events = new List<AgentEvent>();
        session.EventReceived += events.Add;

        await using (session)
        {
            await session.StartAsync();
            await session.SendUserMessageAsync("跑很久");
            await session.InterruptAsync();
            await WaitUntil(() => events.OfType<TurnCompletedEvent>().Any(), TimeSpan.FromSeconds(20));
        }

        // 被中断的轮次不是失败：不应出现 process_exited 之外的错误事件
        Assert.DoesNotContain(events, e => e is AgentFaultEvent { Error.Code: "interrupt_no_ack" });
    }

    private static async Task WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("等待事件超时");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CIRNO_CLAUDE_PATH", null);
        Environment.SetEnvironmentVariable("CIRNO_FAKE_CLAUDE_SCRIPT", null);

        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // 临时目录清理失败不影响测试结论
        }
    }
}
