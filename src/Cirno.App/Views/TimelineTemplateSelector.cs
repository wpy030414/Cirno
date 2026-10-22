using Cirno.Agents.Abstractions;
using Cirno.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Cirno.App.Views;

/// <summary>时间线按条目类型选择模板。未知类型走「原始数据」样式，不丢弃内容。</summary>
public sealed partial class TimelineTemplateSelector : DataTemplateSelector
{
    public DataTemplate? UserTemplate { get; set; }

    public DataTemplate? AssistantTemplate { get; set; }

    public DataTemplate? ToolTemplate { get; set; }

    public DataTemplate? ApprovalTemplate { get; set; }

    public DataTemplate? NoticeTemplate { get; set; }

    public DataTemplate? ErrorTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container)
        => SelectTemplateCore(item);

    protected override DataTemplate? SelectTemplateCore(object item)
    {
        if (item is not TimelineItem t)
        {
            return base.SelectTemplateCore(item);
        }

        return t.Kind switch
        {
            AgentItemKind.UserMessage => UserTemplate,
            AgentItemKind.AssistantMessage => AssistantTemplate,
            AgentItemKind.Approval => ApprovalTemplate,
            AgentItemKind.Notice => NoticeTemplate,
            AgentItemKind.Error or AgentItemKind.Unknown => ErrorTemplate,
            _ => ToolTemplate,
        };
    }
}
