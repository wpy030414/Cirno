using System.Text.RegularExpressions;

namespace Cirno.Agents.Claude;

/// <summary>
/// 用户转录文本的机器块清洗。
/// <para>
/// Claude Code 会把宿主侧的动作写进转录的 user 行：斜杠命令回显（<c>&lt;command-name&gt;</c> 等）、
/// 本地命令回显（<c>&lt;local-command-*&gt;</c>）、宿主提醒（<c>&lt;system-reminder&gt;</c>）。
/// 这些行没有统一的 <c>isMeta</c>/<c>origin</c> 特征（实测 <c>/clear</c> 行两者皆无，
/// 自定义命令行的 origin 反而是 human），只能按内容识别。
/// </para>
/// <para>只用于读取路径（历史标题与转录导入）；用户实际发送的文本绝不经过这里。</para>
/// </summary>
internal static class ClaudeUserText
{
    /// <summary>已知的机器块标签。新增格式时只改这一处。</summary>
    private static readonly string[] BlockTags =
    [
        "command-name",
        "command-message",
        "command-args",
        "local-command-caveat",
        "local-command-stdout",
        "system-reminder",
    ];

    /// <summary>成对标签整块删除：只删标签会把命令文本留在正文里。</summary>
    private static readonly Regex SyntheticBlock = new(
        @"<(?<tag>" + string.Join('|', BlockTags) + @")\b[^>]*>.*?</\k<tag>\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>未闭合的已知开标签：保守起见整段视为机器文本，不做半截展示。</summary>
    private static readonly Regex UnclosedSyntheticStart = new(
        @"^<(" + string.Join('|', BlockTags) + @")\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// 剥离已知机器块并 Trim；剥离后为空（只剩机器块）时返回 null。
    /// 真实 <c>/clear</c> 行带 12 空格缩进，Trim 是判空的必要步骤。
    /// </summary>
    public static string? Sanitize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var text = SyntheticBlock.Replace(raw, string.Empty).Trim();
        if (text.Length == 0 || UnclosedSyntheticStart.IsMatch(text))
        {
            return null;
        }

        return text;
    }
}
