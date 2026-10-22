namespace Cirno.App;

/// <summary>
/// 一次「启动意图」：新实例把它转交给已有实例，已有实例据此动作。
/// 用最简的 key=value 文本，避免跨进程引入序列化依赖。
/// </summary>
public sealed record ActivationRequest
{
    /// <summary>是否要求新建会话。</summary>
    public bool NewSession { get; init; }

    /// <summary>指定工作目录（可空）。</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>要发送的初始提问（可空）。</summary>
    public string? Prompt { get; init; }

    /// <summary>是否只把窗口带到前台。</summary>
    public bool BringToFront { get; init; } = true;

    public static ActivationRequest Empty => new() { BringToFront = true };

    public string Serialize() => string.Join(
        '\u001f',
        $"new={(NewSession ? 1 : 0)}",
        $"cwd={Uri.EscapeDataString(WorkingDirectory ?? string.Empty)}",
        $"prompt={Uri.EscapeDataString(Prompt ?? string.Empty)}",
        $"front={(BringToFront ? 1 : 0)}");

    public static ActivationRequest Parse(string payload)
    {
        var request = Empty;

        foreach (var part in payload.Split('\u001f', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = part[..separator];
            var value = part[(separator + 1)..];

            switch (key)
            {
                case "new":
                    request = request with { NewSession = value == "1" };
                    break;
                case "cwd":
                    request = request with
                    {
                        WorkingDirectory = string.IsNullOrEmpty(value)
                            ? null
                            : Uri.UnescapeDataString(value),
                    };
                    break;
                case "prompt":
                    request = request with
                    {
                        Prompt = string.IsNullOrEmpty(value) ? null : Uri.UnescapeDataString(value),
                    };
                    break;
                case "front":
                    request = request with { BringToFront = value == "1" };
                    break;
            }
        }

        return request;
    }

    /// <summary>从当前进程的命令行解析启动意图。</summary>
    public static ActivationRequest FromCommandLine()
    {
        var args = Environment.GetCommandLineArgs();

        string? Read(string name)
        {
            for (var i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                {
                    return args[i + 1];
                }
            }

            return null;
        }

        var hasFlag = args.Any(a => string.Equals(a, "--new-session", StringComparison.OrdinalIgnoreCase));

        return new ActivationRequest
        {
            NewSession = hasFlag,
            WorkingDirectory = Read("--cwd"),
            Prompt = Read("--prompt"),
            BringToFront = true,
        };
    }
}
