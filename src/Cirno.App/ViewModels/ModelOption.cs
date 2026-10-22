namespace Cirno.App.ViewModels;

/// <summary>
/// 模型选项。Claude Code 没有模型清单能力，这里只列 CLI 认可的别名；
/// 「跟随默认」表示不传 --model，由后端配置决定。
/// </summary>
public sealed record ModelOption(string Label, string? Value)
{
    public override string ToString() => Label;
}
