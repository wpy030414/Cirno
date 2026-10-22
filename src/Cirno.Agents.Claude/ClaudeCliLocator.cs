namespace Cirno.Agents.Claude;

/// <summary>
/// 定位 <c>claude</c> 可执行文件。
/// 优先原生 exe（可直接重定向 stdin/stdout，不经 shell）；
/// 只有 .cmd/.bat 时才退回 cmd.exe，因为经 shell 转发会引入引号转义问题。
/// </summary>
public static class ClaudeCliLocator
{
    /// <summary>返回可执行文件与需要的参数前缀；找不到时返回 null。</summary>
    public static (string FileName, string ArgumentPrefix)? Locate()
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
                return (exe, string.Empty);
            }
        }

        // 2) npm 全局安装布局下的原生 exe
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var npmExe = Path.Combine(appData, "npm", "node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe");
        if (File.Exists(npmExe))
        {
            return (npmExe, string.Empty);
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

    private static (string, string) Describe(string path)
    {
        var ext = Path.GetExtension(path);
        if (ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".bat", StringComparison.OrdinalIgnoreCase))
        {
            // /d 跳过 AutoRun，/s 保留引号语义，/c 执行后退出
            return ("cmd.exe", $"/d /s /c \"\"{path}\"\"");
        }

        return (path, string.Empty);
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
}
