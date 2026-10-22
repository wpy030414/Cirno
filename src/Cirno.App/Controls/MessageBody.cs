using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;

namespace Cirno.App.Controls;

/// <summary>
/// 消息正文渲染：Markdown 的**子集**直接映射为原生 XAML。
/// 支持围栏代码块、标题、无序/有序列表、加粗与行内代码；
/// 其余语法按纯文本呈现并保留原字符，不做静默丢弃。
/// 主链路刻意不引入 WebView2 —— 见 ADR-103。
/// </summary>
public sealed class MessageBody : ContentControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text),
        typeof(string),
        typeof(MessageBody),
        new PropertyMetadata(string.Empty, OnTextChanged));

    public static readonly DependencyProperty IsStreamingProperty = DependencyProperty.Register(
        nameof(IsStreaming),
        typeof(bool),
        typeof(MessageBody),
        new PropertyMetadata(false, OnTextChanged));

    public MessageBody()
    {
        IsTabStop = false;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
    }

    /// <summary>流式期间走纯文本快路径：不解析 Markdown、不重建控件树。</summary>
    public bool IsStreaming
    {
        get => (bool)GetValue(IsStreamingProperty);
        set => SetValue(IsStreamingProperty, value);
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((MessageBody)d).Rebuild();

    private void Rebuild()
    {
        var text = Text ?? string.Empty;

        // 流式期间每个合帧周期都会变文本：只更新一个 TextBlock，
        // 等流结束再解析 Markdown。否则长回答会持续重建控件树。
        if (IsStreaming)
        {
            if (Content is TextBlock { Tag: "stream" } streamingBlock)
            {
                streamingBlock.Text = text;
            }
            else
            {
                Content = new TextBlock
                {
                    Tag = "stream",
                    Text = text,
                    Style = StyleOf("MessageBodyTextStyle"),
                    IsTextSelectionEnabled = true,
                    TextWrapping = TextWrapping.Wrap,
                };
            }

            return;
        }

        var panel = new StackPanel { Spacing = 6 };

        if (text.Length == 0)
        {
            Content = panel;
            return;
        }

        var lines = text.Replace("\r\n", "\n").Split('\n');
        var paragraph = new List<string>();
        var index = 0;

        void FlushParagraph()
        {
            if (paragraph.Count == 0)
            {
                return;
            }

            panel.Children.Add(BuildParagraph(paragraph));
            paragraph.Clear();
        }

        while (index < lines.Length)
        {
            var line = lines[index];

            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                FlushParagraph();
                var language = line.Trim().Trim('`').Trim();
                var code = new List<string>();
                index++;
                while (index < lines.Length &&
                       !lines[index].TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    code.Add(lines[index]);
                    index++;
                }

                index++; // 跳过结束围栏
                panel.Children.Add(BuildCodeBlock(string.Join('\n', code), language));
                continue;
            }

            if (line.Length == 0)
            {
                FlushParagraph();
                index++;
                continue;
            }

            paragraph.Add(line);
            index++;
        }

        FlushParagraph();
        Content = panel;
    }

    private static TextBlock BuildParagraph(List<string> lines)
    {
        var first = lines[0];
        var headingLevel = 0;
        while (headingLevel < first.Length && first[headingLevel] == '#')
        {
            headingLevel++;
        }

        var block = new TextBlock
        {
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.Wrap,
            Style = StyleOf("MessageBodyTextStyle"),
        };

        if (headingLevel is >= 1 and <= 6 && first.Length > headingLevel && first[headingLevel] == ' ')
        {
            block.FontSize = headingLevel switch
            {
                1 => 19,
                2 => 17,
                _ => 15,
            };
            block.FontWeight = FontWeights.SemiBold;
            block.Margin = new Thickness(0, 6, 0, 2);
            AppendInlines(block, first[(headingLevel + 1)..]);
            return block;
        }

        var normalized = lines.Select(NormalizeListLine).ToList();
        AppendInlines(block, string.Join('\n', normalized));
        return block;
    }

    private static string NormalizeListLine(string line)
    {
        var trimmed = line.TrimStart();
        var indent = line[..(line.Length - trimmed.Length)];

        if (trimmed.StartsWith("- ", StringComparison.Ordinal) ||
            trimmed.StartsWith("* ", StringComparison.Ordinal) ||
            trimmed.StartsWith("+ ", StringComparison.Ordinal))
        {
            return indent + "• " + trimmed[2..];
        }

        return line;
    }

    private static Border BuildCodeBlock(string code, string language)
    {
        var stack = new StackPanel { Spacing = 6 };

        if (!string.IsNullOrWhiteSpace(language))
        {
            stack.Children.Add(new TextBlock
            {
                Text = language,
                FontSize = 11,
                IsTextSelectionEnabled = false,
                Style = StyleOf("CodeBlockLabelStyle"),
            });
        }

        stack.Children.Add(new TextBlock
        {
            Text = code,
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.Wrap,
            Style = StyleOf("CodeBlockTextStyle"),
        });

        return new Border
        {
            Style = StyleOf("CodeBlockBorderStyle"),
            Child = stack,
        };
    }

    /// <summary>只处理加粗与行内代码；其余字符原样保留。</summary>
    private static void AppendInlines(TextBlock block, string text)
    {
        var i = 0;
        while (i < text.Length)
        {
            var bold = text.IndexOf("**", i, StringComparison.Ordinal);
            var code = text.IndexOf('`', i);

            var next = -1;
            var isBold = false;

            if (bold >= 0 && (code < 0 || bold < code))
            {
                next = bold;
                isBold = true;
            }
            else if (code >= 0)
            {
                next = code;
            }

            if (next < 0)
            {
                block.Inlines.Add(new Run { Text = text[i..] });
                return;
            }

            if (next > i)
            {
                block.Inlines.Add(new Run { Text = text[i..next] });
            }

            if (isBold)
            {
                var end = text.IndexOf("**", next + 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    block.Inlines.Add(new Run { Text = text[next..] });
                    return;
                }

                var boldElement = new Bold();
                boldElement.Inlines.Add(new Run { Text = text[(next + 2)..end] });
                block.Inlines.Add(boldElement);
                i = end + 2;
            }
            else
            {
                var end = text.IndexOf('`', next + 1);
                if (end < 0)
                {
                    block.Inlines.Add(new Run { Text = text[next..] });
                    return;
                }

                block.Inlines.Add(new Run
                {
                    Text = text[(next + 1)..end],
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                });
                i = end + 1;
            }
        }
    }

    private static Style? StyleOf(string key)
        => Application.Current.Resources.TryGetValue(key, out var value) ? value as Style : null;
}
