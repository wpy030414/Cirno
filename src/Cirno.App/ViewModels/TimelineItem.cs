using Cirno.Agents.Abstractions;
using Microsoft.UI.Xaml;

namespace Cirno.App.ViewModels;

public enum ApprovalState
{
    Pending,
    Allowed,
    Denied,
}

/// <summary>时间线条目。视图只读这些属性，不接触后端载荷。</summary>
public sealed class TimelineItem : ObservableObject
{
    private string _text = string.Empty;
    private string? _detail;
    private bool _isStreaming;
    private bool _isError;
    private ApprovalState _approvalState = ApprovalState.Pending;

    public TimelineItem(AgentItem item)
    {
        Id = item.Id;
        Kind = item.Kind;
        ToolName = item.ToolName;
        _text = item.Text ?? string.Empty;
        _detail = item.Detail;
        _isStreaming = item.IsStreaming;
        _isError = item.IsError;
        Timestamp = item.Timestamp;
    }

    public string Id { get; }

    public AgentItemKind Kind { get; }

    public string? ToolName { get; }

    public DateTimeOffset Timestamp { get; }

    public string Text
    {
        get => _text;
        set
        {
            if (Set(ref _text, value))
            {
                OnPropertyChanged(nameof(HasText));
            }
        }
    }

    public string? Detail
    {
        get => _detail;
        set
        {
            if (Set(ref _detail, value))
            {
                OnPropertyChanged(nameof(HasDetail));
                OnPropertyChanged(nameof(Summary));
            }
        }
    }

    /// <summary>折叠状态下的一行摘要：详情首个非空行，超长截断。</summary>
    public string Summary
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_detail))
            {
                return string.Empty;
            }

            var first = _detail
                .Split('\n')
                .Select(l => l.Trim())
                .FirstOrDefault(l => l.Length > 0) ?? string.Empty;

            return first.Length <= 96 ? first : first[..96] + "…";
        }
    }

    public bool IsStreaming
    {
        get => _isStreaming;
        set => Set(ref _isStreaming, value);
    }

    public bool IsError
    {
        get => _isError;
        set => Set(ref _isError, value);
    }

    public ApprovalState ApprovalState
    {
        get => _approvalState;
        set
        {
            if (Set(ref _approvalState, value))
            {
                OnPropertyChanged(nameof(IsApprovalPending));
                OnPropertyChanged(nameof(ApprovalActionsVisibility));
                OnPropertyChanged(nameof(ApprovalResultText));
                OnPropertyChanged(nameof(ApprovalResultVisibility));
            }
        }
    }

    public bool HasText => _text.Length > 0;

    public bool HasDetail => !string.IsNullOrWhiteSpace(_detail);

    public bool IsApprovalPending => _approvalState == ApprovalState.Pending;

    public Visibility ApprovalActionsVisibility =>
        IsApprovalPending ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ApprovalResultVisibility =>
        IsApprovalPending ? Visibility.Collapsed : Visibility.Visible;

    public string ApprovalResultText => _approvalState switch
    {
        ApprovalState.Allowed => "已允许",
        ApprovalState.Denied => "已拒绝",
        _ => string.Empty,
    };

    /// <summary>工具条上的标签：命令、文件、工具…</summary>
    public string KindLabel => Kind switch
    {
        AgentItemKind.CommandExecution => "命令",
        AgentItemKind.FileChange => "文件变更",
        AgentItemKind.ToolCall => ToolName ?? "工具",
        AgentItemKind.Plan => "计划",
        AgentItemKind.Reasoning => "思考",
        _ => string.Empty,
    };

    public string HeaderLine => string.IsNullOrWhiteSpace(ToolName) ? KindLabel : ToolName!;

    public void Apply(AgentItem item)
    {
        Text = item.Text ?? Text;
        if (!string.IsNullOrWhiteSpace(item.Detail))
        {
            Detail = item.Detail;
        }

        IsStreaming = item.IsStreaming;
        IsError = item.IsError;
    }
}
