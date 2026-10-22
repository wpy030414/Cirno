using System.Diagnostics;
using Cirno.Agents.Abstractions;

namespace Cirno.Agents.Claude;

/// <summary>Claude Code 后端。provider 是进程级属性：一个会话一个进程，进程即会话。</summary>
public sealed class ClaudeBackend : IAgentBackend
{
    public string Id => "claude";

    public string DisplayName => "Claude Code";

    public async Task<string?> ProbeAsync(CancellationToken cancellationToken = default)
    {
        var located = ClaudeCliLocator.Locate();
        if (located is null)
        {
            return "未找到 claude 可执行文件。请安装 Claude Code（npm i -g @anthropic-ai/claude-code），"
                 + "或用环境变量 CIRNO_CLAUDE_PATH 指定路径。";
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = located.FileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            ClaudeCliLocator.ApplyArguments(psi, located, ["--version"]);

            using var process = Process.Start(psi);
            if (process is null)
            {
                return "无法启动 claude。";
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));

            var stdout = await process.StandardOutput.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);

            Version = stdout.Trim();
            return process.ExitCode == 0 ? null : $"claude 探测失败（退出码 {process.ExitCode}）。";
        }
        catch (OperationCanceledException)
        {
            return "claude 探测超时。";
        }
        catch (Exception ex)
        {
            return $"claude 探测失败：{ex.Message}";
        }
    }

    /// <summary>最近一次探测到的后端版本，写入会话元数据。</summary>
    public string? Version { get; private set; }

    public IAgentSession CreateSession(AgentSessionOptions options) => new ClaudeSession(options);
}
