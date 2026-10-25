namespace Cirno.App.Services;

/// <summary>
/// 侧栏副标题用的相对时间格式化。
/// 刻意选了短形态（「14 分钟前」「昨天」「10月20日」）——副标题空间有限，
/// 精确时间放 tooltip（<see cref="FormatExact"/>）。
/// </summary>
public static class RelativeTime
{
    /// <summary>刚刚 / N 分钟前 / N 小时前（当天）/ 昨天 / M月d日 / yyyy年M月d日（跨年）。</summary>
    public static string Format(DateTimeOffset time, DateTimeOffset? now = null)
    {
        var current = now ?? DateTimeOffset.Now;
        var local = time.ToLocalTime();

        var delta = current - local;
        if (delta < TimeSpan.Zero)
        {
            // 文件时钟与本地时钟的微小偏移，或未来时间：一律算「刚刚」
            delta = TimeSpan.Zero;
        }

        if (delta.TotalMinutes < 1)
        {
            return "刚刚";
        }

        if (delta.TotalHours < 1)
        {
            return $"{(int)delta.TotalMinutes} 分钟前";
        }

        if (local.Date == current.Date)
        {
            return $"{(int)delta.TotalHours} 小时前";
        }

        if (local.Date == current.Date.AddDays(-1))
        {
            return "昨天";
        }

        return local.Year == current.Year
            ? $"{local.Month}月{local.Day}日"
            : $"{local.Year}年{local.Month}月{local.Day}日";
    }

    /// <summary>精确时间（tooltip 用）。</summary>
    public static string FormatExact(DateTimeOffset time)
        => time.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
}
