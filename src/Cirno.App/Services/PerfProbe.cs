using System.Diagnostics;
using System.Text.Json;
using Cirno.Agents.Abstractions;
using Cirno.App.ViewModels;
using Cirno.Core.Orchestration;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Cirno.App.Services;

/// <summary>
/// 万条消息的界面性能自检。
/// <para>
/// 存储层的数字不够——真正要回答的是「一万条消息的会话，用户点开后多久看到内容、
/// 滚动跟不跟手、内存占多少」。这里在真实窗口里跑一遍：造数据 → 还原 → 绑定 → 滚动。
/// </para>
/// <para>
/// 用法：<c>Cirno.exe --perf 10000 --perf-report out.json</c>
/// </para>
/// </summary>
public sealed class PerfProbe
{
    private readonly MainWindow _window;
    private readonly int _itemCount;

    public PerfProbe(MainWindow window, int itemCount)
    {
        _window = window;
        _itemCount = itemCount;
    }

    public async Task<ProbeReport> RunAsync()
    {
        var report = new ProbeReport { ItemCount = _itemCount };

        var orchestrator = _window.PerfOrchestrator
            ?? throw new InvalidOperationException("性能自检需要一个可写的编排层。");

        // 1) 造数据：一次事务写入，模拟一个「很长的会话」
        var session = orchestrator.CreateSession("claude", Environment.CurrentDirectory, "perf");
        var seedWatch = Stopwatch.StartNew();
        orchestrator.AppendItems(session.Id, Enumerable.Range(0, _itemCount).Select(CreateItem));
        seedWatch.Stop();
        report.SeedMs = seedWatch.Elapsed.TotalMilliseconds;

        // 2) 还原：等价于用户点开这个会话
        var vm = new SessionViewModel(
            orchestrator,
            session,
            _window.DispatcherQueue,
            autoApprove: false,
            tracePath: null);

        var restoreWatch = Stopwatch.StartNew();
        await vm.LoadHistoryAsync();
        restoreWatch.Stop();
        report.RestoreMs = restoreWatch.Elapsed.TotalMilliseconds;
        report.RestoredItems = vm.Items.Count;

        // 3) 绑定：真实 ListView 接上这一万条，测布局代价
        var list = _window.TimelineControl;
        var previous = list.ItemsSource;
        list.ItemsSource = vm.Items;

        var bindWatch = Stopwatch.StartNew();
        list.UpdateLayout();
        bindWatch.Stop();
        report.BindMs = bindWatch.Elapsed.TotalMilliseconds;

        // 4) 滚动：定位到最后一条（用户最常见的「跳到最新」）
        var scrollWatch = Stopwatch.StartNew();
        if (vm.Items.Count > 0)
        {
            list.ScrollIntoView(vm.Items[^1]);
            list.UpdateLayout();
        }

        scrollWatch.Stop();
        report.ScrollToEndMs = scrollWatch.Elapsed.TotalMilliseconds;

        // 5) 往返滚动：滚到中段再滚回来，测交互跟手程度
        var middle = vm.Items.Count / 2;
        var roundTripWatch = Stopwatch.StartNew();
        if (middle > 0)
        {
            list.ScrollIntoView(vm.Items[middle]);
            list.UpdateLayout();
            list.ScrollIntoView(vm.Items[^1]);
            list.UpdateLayout();
        }

        roundTripWatch.Stop();
        report.ScrollRoundTripMs = roundTripWatch.Elapsed.TotalMilliseconds;

        // 6) 内存：还原后托管堆的增量
        GC.Collect();
        GC.WaitForPendingFinalizers();
        report.ManagedHeapBytes = GC.GetTotalMemory(forceFullCollection: true);

        // 复原界面
        list.ItemsSource = previous;

        await Task.CompletedTask;
        return report;
    }

    /// <summary>构造有代表性的条目：文本 + 详情混合。</summary>
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
            Id = $"perf:{index}",
            Kind = kind,
            Text = $"第 {index} 条消息：这是一段用于界面性能自检的正文，长度接近真实回复。",
            Detail = kind is AgentItemKind.FileChange
                ? string.Join('\n', Enumerable.Range(0, 20).Select(l => $"- 第 {l} 行改动"))
                : null,
            ToolName = kind is AgentItemKind.ToolCall ? "Bash" : null,
            Timestamp = DateTimeOffset.Now,
        };
    }
}

/// <summary>界面性能自检的结果。</summary>
public sealed record ProbeReport
{
    public int ItemCount { get; init; }

    public double SeedMs { get; set; }

    public double RestoreMs { get; set; }

    public int RestoredItems { get; set; }

    public double BindMs { get; set; }

    public double ScrollToEndMs { get; set; }

    public double ScrollRoundTripMs { get; set; }

    public long ManagedHeapBytes { get; set; }

    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
}
