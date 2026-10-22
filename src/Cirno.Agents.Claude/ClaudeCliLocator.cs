using System.Diagnostics;

namespace Cirno.Agents.Claude;

/// <summary>一次 claude 启动命令。cmd 脚本需要包一层 shell。</summary>
/// <param name="FileName">实际可执行文件。</param>
/// <param name="ArgumentPrefix">需要在业务参数之前插入的前缀（仅 cmd 脚本分支有值）。</param>
/// <param name="ViaCmd">是否经过 cmd.exe 转发。</param>
public sealed record ClaudeCliCommand(string FileName, string? ArgumentPrefix, bool ViaCmd);

/// <summary>
/// 定位 <c>claude</c> 可执行文件。
/// 优先原生 exe（可直接重定向 stdin/stdout，不经 shell）；
/// 只有 .cmd/.bat 时才退回 cmd.exe，因为经 shell 转发会引入引号转义问题。
/// </summary>
public static class ClaudeCliLocator
{
    /// <summary>返回启动命令；找不到时返回 null。</summary>
    public static ClaudeCliCommand? Locate()
    {
        var overridePath = Environment.GetEnvironmentVariable("CIRNO_CLAUDE_PATH");
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
        {
            return Describe(overridePath);
        }

        // 1) PATH 中的原生 exe
        foreach (var dir in EnumeratePathDirectories())
        {
            var exe = Path.Combine(dir, "claude.exe");
            if (File.Exists(exe))
            {
                return new ClaudeCliCommand(exe, null, ViaCmd: false);
            }
        }

        // 2) npm 全局安装布局下的原生 exe
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var npmExe = Path.Combine(appData, "npm", "node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe");
        if (File.Exists(npmExe))
        {
            return new ClaudeCliCommand(npmExe, null, ViaCmd: false);
        }

        // 3) 只有脚本 shim：经 cmd.exe 启动
        foreach (var dir in EnumeratePathDirectories())
        {
            foreach (var name in new[] { "claude.cmd", "claude.bat" })
            {
                var shim = Path.Combine(dir, name);
                if (File.Exists(shim))
                {
                    return Describe(shim);
                }
            }
        }

        return null;
    }

    private static ClaudeCliCommand Describe(string path)
    {
        var ext = Path.GetExtension(path);
        if (ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".bat", StringComparison.OrdinalIgnoreCase))
        {
            // /d 跳过 AutoRun，/s 保留引号语义，/c 执行后退出
            return new ClaudeCliCommand("cmd.exe", $"/d /s /c \"\"{path}\"\"", ViaCmd: true);
        }

        return new ClaudeCliCommand(path, null, ViaCmd: false);
    }

    private static IEnumerable<string> EnumeratePathDirectories()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var raw in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var dir = raw.Trim().Trim('"');
            if (dir.Length > 0 && Directory.Exists(dir))
            {
                yield return dir;
            }
        }
    }

    /// <summary>
    /// 把业务参数拼成一条命令行。
    /// cmd 转发分支必须用单一字符串（cmd 的 /c 语义），其余分支用参数列表更安全。
    /// </summary>
    public static void ApplyArguments(ProcessStartInfo psi, ClaudeCliCommand command, IEnumerable<string> args)
    {
        var list = args.ToList();

        if (!command.ViaCmd)
        {
            foreach (var arg in list)
            {
                psi.ArgumentList.Add(arg);
            }

            return;
        }

        var joined = string.Join(' ', list.Select(Quote));
        psi.Arguments = command.ArgumentPrefix + " " + joined;
    }

    private static string Quote(string value)
    {
        if (value.Length > 0 && !value.Any(char.IsWhiteSpace) && !value.Contains('"'))
        {
            return value;
        }

        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }
}
