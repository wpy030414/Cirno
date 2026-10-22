using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Cirno.App.Controls;

/// <summary>
/// 文件变更展示：按行着色（新增 / 删除 / 上下文）。
/// 不做行号与折叠——这里的目标是「一眼看出改了什么」，不是代码审阅器。
/// </summary>
public sealed class DiffBody : ContentControl
{
    private const int MaxLines = 600;

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text),
        typeof(string),
        typeof(DiffBody),
        new PropertyMetadata(string.Empty, OnTextChanged));

    public DiffBody()
    {
        IsTabStop = false;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((DiffBody)d).Rebuild();

    private void Rebuild()
    {
        var panel = new StackPanel { Spacing = 0 };
        var text = (Text ?? string.Empty).Replace("\r\n", "\n");

        if (text.Length == 0)
        {
            Content = panel;
            return;
        }

        var lines = text.Split('\n');
        var shown = 0;

        foreach (var line in lines)
        {
            if (shown++ >= MaxLines)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = $"… 其余 {lines.Length - MaxLines} 行已折叠",
                    FontSize = 11,
                    Style = StyleOf("MessageBodyMetaStyle"),
                });
                break;
            }

            var (textStyleKey, rowStyleKey) = Classify(line);
            var lineBlock = new TextBlock
            {
                Text = line,
                Style = StyleOf(textStyleKey),
                TextWrapping = TextWrapping.Wrap,
            };

            if (rowStyleKey is null)
            {
                panel.Children.Add(lineBlock);
            }
            else
            {
                panel.Children.Add(new Border
                {
                    Style = StyleOf(rowStyleKey),
                    Child = lineBlock,
                });
            }
        }

        Content = new Border
        {
            Style = StyleOf("CodeBlockBorderStyle"),
            Child = panel,
        };
    }

    private static (string TextStyleKey, string? RowStyleKey) Classify(string line)
        => line switch
        {
            _ when line.StartsWith("+ ", StringComparison.Ordinal) => ("DiffAddTextStyle", "DiffAddRowStyle"),
            _ when line.StartsWith("- ", StringComparison.Ordinal) => ("DiffRemoveTextStyle", "DiffRemoveRowStyle"),
            _ => ("DiffContextTextStyle", null),
        };

    private static Style? StyleOf(string key)
        => Application.Current.Resources.TryGetValue(key, out var value) ? value as Style : null;
}
