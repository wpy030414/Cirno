using System.Diagnostics;
using Cirno.Agents.Abstractions;
using Cirno.Core.Models;
using Cirno.Core.Persistence;
using Xunit;

namespace Cirno.Core.Tests;

/// <summary>
/// 存储层的万条消息护栏。
/// <para>
/// M5 的出口条件要求「万条消息会话可还原」。这里把那条线固化成可回归的数字：
/// 万条写入与读取的耗时上限。阈值留了余量，但足以拦住「把 O(n²) 写回 O(n)」
/// 这类回退（例如序号改回每条查 MAX(seq)）。
/// </para>
/// </summary>
public sealed class SessionStorePerformanceTests : IDisposable
{
    private const int ItemCount = 10_000;

    private readonly string _databasePath;

    public SessionStorePerformanceTests()
    {
        _databasePath = Path.Combine(
            Path.GetTempPath(),
            "cirno-core-tests",
            $"{Guid.NewGuid():n}.db");
    }

    [Fact]
    public void 万条消息_写入应在预算内完成()
    {
        using var store = new SessionStore(_databasePath);
        var session = CreateSession();
        var items = Enumerable.Range(0, ItemCount).Select(CreateItem);

        var watch = Stopwatch.StartNew();
        store.AppendItems(session.Id, items);
        watch.Stop();

        Assert.True(
            watch.ElapsedMilliseconds < 2000,
            $"批量写入 {ItemCount} 条耗时 {watch.ElapsedMilliseconds} ms，超出 2000 ms 预算");
    }

    [Fact]
    public void 万条消息_读取应在预算内完成()
    {
        using var store = new SessionStore(_databasePath);
        var session = CreateSession();
        store.AppendItems(session.Id, Enumerable.Range(0, ItemCount).Select(CreateItem));
        store.Checkpoint();

        var watch = Stopwatch.StartNew();
        var loaded = store.LoadItems(session.Id);
        watch.Stop();

        Assert.Equal(ItemCount, loaded.Count);
        Assert.True(
            watch.ElapsedMilliseconds < 1000,
            $"读取 {ItemCount} 条耗时 {watch.ElapsedMilliseconds} ms，超出 1000 ms 预算");
    }

    [Fact]
    public void 逐条写入与批量写入_结果一致()
    {
        using var store = new SessionStore(_databasePath);
        var session = CreateSession();

        store.AppendItems(session.Id, Enumerable.Range(0, 100).Select(CreateItem));

        var loaded = store.LoadItems(session.Id);
        Assert.Equal(100, loaded.Count);

        // 顺序号必须连续且递增：界面按 seq 还原时间线
        var sequences = store.LoadItemSequences(session.Id);
        Assert.Equal(Enumerable.Range(1, 100).Select(i => (long)i), sequences);
    }

    [Fact]
    public void 相同条目重复写入_不产生重复行()
    {
        using var store = new SessionStore(_databasePath);
        var session = CreateSession();

        store.AppendItem(session.Id, CreateItem(0));
        store.AppendItem(session.Id, CreateItem(0));

        Assert.Single(store.LoadItems(session.Id));
    }

    public void Dispose()
    {
        try
        {
            if (File.Exists(_databasePath))
            {
                File.Delete(_databasePath);
            }
        }
        catch (IOException)
        {
            // 临时文件残留不影响测试结论
        }

        GC.SuppressFinalize(this);
    }

    private static SessionRecord CreateSession() => new()
    {
        Id = Guid.NewGuid().ToString(),
        ProviderId = "claude",
        WorkingDirectory = Environment.CurrentDirectory,
        CreatedAt = DateTimeOffset.Now,
        UpdatedAt = DateTimeOffset.Now,
    };

    private static AgentItem CreateItem(int index) => new()
    {
        Id = $"item:{index}",
        Kind = AgentItemKind.AssistantMessage,
        Text = $"第 {index} 条消息：这是一段用于基准测试的正文，长度接近真实回复。",
        Timestamp = DateTimeOffset.Now,
    };
}
