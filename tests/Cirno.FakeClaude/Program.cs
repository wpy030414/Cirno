using System.Text;
using System.Text.Json;

// 假的 claude CLI：按环境变量指定的剧本，把预录的 stream-json 逐行吐到 stdout，
// 并在收到宿主发来的 control_request（initialize / interrupt）时回一条 control_response。
// 用途：端到端验证 ClaudeSession 的进程、管道与控制通道，不依赖真实 CLI 与网络。
var script = Environment.GetEnvironmentVariable("CIRNO_FAKE_CLAUDE_SCRIPT");
if (string.IsNullOrWhiteSpace(script))
{
    Console.Error.WriteLine("CIRNO_FAKE_CLAUDE_SCRIPT 未设置");
    return 2;
}

var lines = File.ReadAllLines(script)
    .Where(l => l.Trim().Length > 0)
    .ToArray();

var reader = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);

// 后台读 stdin：把宿主的控制请求应答掉，避免管道写满或宿主等不到确认。
var readerTask = Task.Run(async () =>
{
    string? line;
    while ((line = await reader.ReadLineAsync()) is not null)
    {
        if (!line.Contains("\"control_request\"", StringComparison.Ordinal))
        {
            continue;
        }

        try
        {
            using var doc = JsonDocument.Parse(line);
            var requestId = doc.RootElement.GetProperty("request_id").GetString();
            var response = new
            {
                type = "control_response",
                response = new
                {
                    subtype = "success",
                    request_id = requestId,
                    response = new { behavior = "allow" },
                },
            };
            Console.Out.WriteLine(JsonSerializer.Serialize(response));
            Console.Out.Flush();
        }
        catch
        {
            // 忽略无法解析的控制帧
        }
    }
});

foreach (var line in lines)
{
    Console.Out.WriteLine(line);
    Console.Out.Flush();
    await Task.Delay(10);
}

// 剧本放完后最多再活一会儿，等宿主把控制应答写进来；随后自行退出。
await Task.WhenAny(readerTask, Task.Delay(TimeSpan.FromSeconds(10)));
return 0;
