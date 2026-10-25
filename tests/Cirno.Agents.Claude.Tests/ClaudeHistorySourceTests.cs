using Cirno.Agents.Abstractions;
using Xunit;

namespace Cirno.Agents.Claude.Tests;

/// <summary>
/// ClaudeHistorySource 的目录扫描与转录读取：
/// 在临时目录里伪造 projects 布局，recycle deleter 用假实现注入，不碰真实回收站。
/// </summary>
public sealed class ClaudeHistorySourceTests : IDisposable
{
    private readonly string _root;
    private string? _deletedPath;
    private bool _deleterResult = true;

    public ClaudeHistorySourceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "cirno-history-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
    }

    private ClaudeHistorySource CreateSource()
        => new(_root, path => { _deletedPath = path; return _deleterResult; });

    private static string FixturePath(string name)
        => Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    private string CreateProject(string mungedName)
    {
        var dir = Path.Combine(_root, mungedName);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void CopyFixture(string fixtureName, string projectDir, string fileName)
        => File.Copy(FixturePath(fixtureName), Path.Combine(projectDir, fileName));

    [Fact]
    public void 正常会话_元数据字段完整()
    {
        var projectDir = CreateProject("C--work--demo");
        const string fileName = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee.jsonl";
        CopyFixture("transcript-basic.jsonl", projectDir, fileName);
        var expectedWriteTime = File.GetLastWriteTimeUtc(Path.Combine(projectDir, fileName));

        var summary = Assert.Single(CreateSource().ListSessions());

        Assert.Equal("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", summary.SessionId);
        Assert.Equal("C:\\work\\demo", summary.WorkingDirectory);
        Assert.Equal("修复登录超时", summary.Title); // 末条 ai-title 胜出
        Assert.Equal(DateTimeOffset.Parse("2026-10-20T08:00:01.000Z"), summary.FirstTimestamp);
        Assert.Equal(expectedWriteTime, summary.LastActiveAt);
        Assert.EndsWith(fileName, summary.FilePath);
    }

    [Fact]
    public void 无cwd行_回退munged目录名()
    {
        var projectDir = CreateProject("C--work--fallback");
        File.WriteAllText(
            Path.Combine(projectDir, "11111111-1111-1111-1111-111111111111.jsonl"),
            "{\"type\":\"mode\",\"mode\":\"normal\",\"sessionId\":\"11111111-1111-1111-1111-111111111111\"}\n");

        var summary = Assert.Single(CreateSource().ListSessions());
        Assert.Equal("C--work--fallback", summary.WorkingDirectory);
        Assert.Null(summary.Title); // 没有用户消息也没有标题行
    }

    [Fact]
    public void 非UUID文件名与子目录不被扫描()
    {
        var projectDir = CreateProject("C--work--demo");

        // 非 UUID 文件名：不是会话
        File.WriteAllText(Path.Combine(projectDir, "notes.jsonl"), "{}\n");

        // memory/ 子目录里的 UUID 文件：项目记忆，不是顶层会话
        var memoryDir = Path.Combine(projectDir, "memory");
        Directory.CreateDirectory(memoryDir);
        File.WriteAllText(
            Path.Combine(memoryDir, "22222222-2222-2222-2222-222222222222.jsonl"), "{}\n");

        Assert.Empty(CreateSource().ListSessions());
    }

    [Fact]
    public void 空文件跳过()
    {
        var projectDir = CreateProject("C--work--empty");
        File.WriteAllText(Path.Combine(projectDir, "33333333-3333-3333-3333-333333333333.jsonl"), string.Empty);

        Assert.Empty(CreateSource().ListSessions());
    }

    [Fact]
    public void 根目录不存在_返回空列表()
    {
        var source = new ClaudeHistorySource(Path.Combine(_root, "不存在的根"));
        Assert.Empty(source.ListSessions());
    }

    [Fact]
    public void 命令回显不参与标题_取后一条人类消息()
    {
        var projectDir = CreateProject("C--work--echo");
        CopyFixture(
            "transcript-command-echo.jsonl",
            projectDir,
            "bbbbbbbb-cccc-dddd-eeee-ffffffffffff.jsonl");

        var summary = Assert.Single(CreateSource().ListSessions());

        // /clear 行（无 origin/isMeta）与自定义命令（origin=human）都不是用户说的话
        Assert.Equal("真正的第一条消息", summary.Title);
    }

    [Fact]
    public void ReadTranscript_命令回显不产出用户条目且提醒被剥离()
    {
        var projectDir = CreateProject("C--work--echo");
        CopyFixture(
            "transcript-command-echo.jsonl",
            projectDir,
            "bbbbbbbb-cccc-dddd-eeee-ffffffffffff.jsonl");

        var summary = Assert.Single(CreateSource().ListSessions());
        var transcript = CreateSource().ReadTranscript(summary);

        Assert.NotNull(transcript);
        var users = transcript!.Items
            .Where(i => i.Kind == AgentItemKind.UserMessage)
            .Select(i => i.Text)
            .ToArray();

        Assert.Equal(["真正的第一条消息", "第二条消息"], users);
        Assert.Equal("真正的第一条消息", transcript.Summary.Title);
    }

    [Fact]
    public void NormalizeStoredTitle_清洗旧库命令回显()
    {
        var source = CreateSource();

        Assert.Equal(
            "真正的标题",
            source.NormalizeStoredTitle("真正的标题<command-name>/clear</command-name>"));
        Assert.Null(source.NormalizeStoredTitle(
            "<command-name>/clear</command-name>\n<command-message>clear</command-message>"));
    }

    [Fact]
    public void ReadTranscript_条目按行序排列且sidechain剔除()
    {
        var projectDir = CreateProject("C--work--demo");
        const string fileName = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee.jsonl";
        CopyFixture("transcript-basic.jsonl", projectDir, fileName);

        var summary = Assert.Single(CreateSource().ListSessions());
        var transcript = CreateSource().ReadTranscript(summary);

        Assert.NotNull(transcript);
        var kinds = transcript!.Items.Select(i => i.Kind).ToArray();
        Assert.Equal(
        [
            AgentItemKind.UserMessage,     // u-0001
            AgentItemKind.Reasoning,       // msg_01 thinking
            AgentItemKind.AssistantMessage, // msg_01 text
            AgentItemKind.ToolCall,        // toolu_01（含 tool_result 合并）
            AgentItemKind.AssistantMessage, // msg_02
        ], kinds);

        var tool = transcript.Items.Single(i => i.Kind == AgentItemKind.ToolCall);
        Assert.Equal("Read", tool.ToolName);
        Assert.Contains("timeout: 3000", tool.Detail);

        Assert.Equal(DateTimeOffset.Parse("2026-10-20T08:00:04.000Z"), tool.Timestamp);
        Assert.Equal(DateTimeOffset.Parse("2026-10-20T08:00:05.000Z"), transcript.LastTimestamp);
    }

    [Fact]
    public void ReadTranscript_文件缺失返回null()
    {
        var source = CreateSource();
        var ghost = new HistoricalSessionSummary
        {
            SessionId = "44444444-4444-4444-4444-444444444444",
            WorkingDirectory = "C:\\ghost",
            LastActiveAt = DateTimeOffset.UtcNow,
            FilePath = Path.Combine(_root, "没有这个文件.jsonl"),
        };

        Assert.Null(source.ReadTranscript(ghost));
    }

    [Fact]
    public void TryDeleteTranscript_三态()
    {
        var source = CreateSource();

        // 目录里没有 → NotFound
        Assert.Equal(HistoryDeleteResult.NotFound, source.TryDeleteTranscript("55555555-5555-5555-5555-555555555555"));

        // 有 → deleter 收到路径并返回成功；假 deleter 不动文件，验证「删除动作全部交给 deleter」
        var projectDir = CreateProject("C--work--del");
        var path = Path.Combine(projectDir, "66666666-6666-6666-6666-666666666666.jsonl");
        File.WriteAllText(path, "{}\n");
        Assert.Equal(HistoryDeleteResult.Deleted, source.TryDeleteTranscript("66666666-6666-6666-6666-666666666666"));
        Assert.Equal(path, _deletedPath);
        Assert.True(File.Exists(path));

        // deleter 失败 → Failed（权限、占用等由调用方决定重试）
        _deleterResult = false;
        Assert.Equal(HistoryDeleteResult.Failed, source.TryDeleteTranscript("66666666-6666-6666-6666-666666666666"));
    }
}
