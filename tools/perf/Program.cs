using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Cirno.Agents.Abstractions;
using Cirno.Core.Models;
using Cirno.Core.Persistence;

namespace Cirno.Perf;

/// <summary>
/// 存储层性能基准：验证「万条消息」的会话在本地库里的读写代价。
/// <para>
/// 这是 M5 出口条件的量化护栏——之前只有主观感受，没有可回归的数字。
/// 用法：<c>dotnet run --project tools/perf -- --items 10000</c>
/// </para>
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        var items = ReadInt(args, "--items", 10_000);
        var repeat = ReadInt(args, "--repeat", 3);
        var databasePath = ReadString(args, "--db")
                           ?? Path.Combine(Path.GetTempPath(), "cirno-perf", "perf.db");

        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        if (File.Exists(databasePath))
        {
            File.Delete(databasePath);
        }

        Console.WriteLine($"Cirno 存储层基准：items={items} repeat={repeat} db={databasePath}");

        var write = new List<double>();
        var read = new List<double>();
        long memoryBytes = 0;

        for (var round = 0; round < repeat; round++)
        {
            // 每轮用独立文件：SQLite 释放句柄有延迟，复用同一路径会撞上文件锁
            var roundPath = Path.Combine(
                Path.GetDirectoryName(databasePath)!,
                $"perf-{round}.db");

            using var store = new SessionStore(roundPath);
            var record = CreateSession();

            var writeWatch = Stopwatch.StartNew();
            store.AppendItems(record.Id, Enumerable.Range(0, items).Select(CreateItem));
            writeWatch.Stop();
            write.Add(writeWatch.Elapsed.TotalMilliseconds);

            // 模拟真实场景：应用退出时 WAL 已落盘，之后打开才是稳态读取
            store.Checkpoint();

            var readWatch = Stopwatch.StartNew();
            var loaded = store.LoadItems(record.Id);
            readWatch.Stop();
            read.Add(readWatch.Elapsed.TotalMilliseconds);

            if (loaded.Count != items)
            {
                Console.Error.WriteLine($"读取条数不符：期望 {items}，实际 {loaded.Count}");
                return 1;
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            memoryBytes = GC.GetTotalMemory(forceFullCollection: true);

            Console.WriteLine(
                $"  第 {round + 1} 轮：写入 {writeWatch.Elapsed.TotalMilliseconds:F0} ms，" +
                $"读取 {readWatch.Elapsed.TotalMilliseconds:F0} ms，托管堆 {memoryBytes / 1024 / 1024:F1} MB");
        }

        var report = new
        {
            items,
            repeat,
            writeMs = Round(write),
            readMs = Round(read),
            managedHeapBytes = memoryBytes,
            readPerItemUs = read.Count > 0 ? read.Average() * 1000 / items : 0,
        };

        Console.WriteLine();
        Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

        // 护栏：万条消息的读取必须在 2 秒内完成，否则 UI 还原会明显卡顿
        var budget = items >= 10_000 ? 2000 : 5000;
        var worstRead = read.Max();
        if (worstRead > budget)
        {
            Console.Error.WriteLine($"超出预算：最慢读取 {worstRead:F0} ms > {budget} ms");
            return 2;
        }

        Console.WriteLine($"通过：最慢读取 {worstRead:F0} ms，预算 {budget} ms。");
        return 0;
    }

    private static double[] Round(List<double> values)
        => [.. values.Select(v => Math.Round(v, 1))];

    private static SessionRecord CreateSession() => new()
    {
        Id = Guid.NewGuid().ToString(),
        ProviderId = "claude",
        WorkingDirectory = Environment.CurrentDirectory,
        Title = "基准会话",
        CreatedAt = DateTimeOffset.Now,
        UpdatedAt = DateTimeOffset.Now,
    };

    /// <summary>构造有代表性的条目：文本 + 详情混合，贴近真实时间线体积。</summary>
    private static AgentItem CreateItem(int index)
    {
        var kind = (index % 7) switch
        {
            0 => AgentItemKind.UserMessage,
            1 => AgentItemKind.AssistantMessage,
            2 => AgentItemKind.Reasoning,
            3 => AgentItemKind.CommandExecution,
            4 => AgentItemKind.FileChange,
            5 => AgentItemKind.ToolCall,
            _ => AgentItemKind.Notice,
        };

        return new AgentItem
        {
            Id = $"item:{index}",
            Kind = kind,
            Text = string.Create(CultureInfo.InvariantCulture, $"第 {index} 条消息：这是一段用于基准测试的正文，长度接近真实回复。"),
            Detail = kind is AgentItemKind.FileChange
                ? string.Join('\n', Enumerable.Range(0, 20).Select(l => $"- 第 {l} 行改动"))
                : null,
            ToolName = kind is AgentItemKind.ToolCall ? "Bash" : null,
            Timestamp = DateTimeOffset.Now,
        };
    }

    private static int ReadInt(string[] args, string name, int fallback)
    {
        var value = ReadString(args, name);
        return value is not null && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
    }

    private static string? ReadString(string[] args, string name)
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
}
