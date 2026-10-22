using System.Text;
using System.Text.Json;

namespace Cirno.Agents.Claude;

/// <summary>
/// 把编辑类工具的参数翻译成可读的 diff 文本。
/// 不做真正的行级 diff 算法：Claude Code 的 Edit 已给出 old_string / new_string，
/// 直接按其语义标注即可；Write 是全量覆盖，标为新增。
/// </summary>
internal static class DiffBuilder
{
    private const int MaxChars = 6000;

    public static string DescribeFileChange(string toolName, JsonElement? input)
    {
        if (input is not { } i || i.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        var path = GetString(i, "file_path") ?? GetString(i, "path") ?? "(未知文件)";
        var sb = new StringBuilder();
        sb.AppendLine(path);

        switch (toolName)
        {
            case "Write":
            {
                var content = GetString(i, "content") ?? string.Empty;
                foreach (var line in SplitLines(content))
                {
                    sb.Append("+ ").AppendLine(line);
                }

                break;
            }

            case "Edit":
            {
                var oldText = GetString(i, "old_string") ?? string.Empty;
                var newText = GetString(i, "new_string") ?? string.Empty;
                AppendPair(sb, oldText, newText);
                break;
            }

            case "MultiEdit":
            {
                if (i.TryGetProperty("edits", out var edits) && edits.ValueKind == JsonValueKind.Array)
                {
                    foreach (var edit in edits.EnumerateArray())
                    {
                        AppendPair(
                            sb,
                            GetString(edit, "old_string") ?? string.Empty,
                            GetString(edit, "new_string") ?? string.Empty);
                        sb.AppendLine();
                    }
                }

                break;
            }

            default:
                AppendPair(
                    sb,
                    GetString(i, "old_string") ?? string.Empty,
                    GetString(i, "new_string") ?? string.Empty);
                break;
        }

        var text = sb.ToString().TrimEnd();
        return text.Length <= MaxChars ? text : text[..MaxChars] + "\n… （已截断）";
    }

    private static void AppendPair(StringBuilder sb, string oldText, string newText)
    {
        foreach (var line in SplitLines(oldText))
        {
            sb.Append("- ").AppendLine(line);
        }

        foreach (var line in SplitLines(newText))
        {
            sb.Append("+ ").AppendLine(line);
        }
    }

    private static IEnumerable<string> SplitLines(string text)
        => text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');

    private static string? GetString(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object &&
           element.TryGetProperty(name, out var value) &&
           value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
