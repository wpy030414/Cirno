using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace Cirno.Shell.Windows;

/// <summary>
/// 生成任务栏徽标与托盘图标的 HICON。
/// 输出是小尺寸位图，用 System.Drawing 生成比手写 GDI 位图操作可靠得多；
/// 颜色一律由调用方传入（系统强调色），不在源码里写死。
/// </summary>
internal static class StatusIconFactory
{
    /// <summary>带数字的圆形徽标（16/32px 都能缩放正常）。返回的 HICON 需调用方销毁。</summary>
    public static IntPtr CreateBadge(int count, Color accent)
    {
        var size = 32;
        using var bitmap = new Bitmap(size, size);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            graphics.Clear(Color.Transparent);

            using var fill = new SolidBrush(accent);
            graphics.FillEllipse(fill, 1, 1, size - 2, size - 2);

            var text = count > 9 ? "9+" : count.ToString();
            using var font = new Font("Segoe UI", count > 9 ? 11f : 14f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var textBrush = new SolidBrush(ContrastColor(accent));
            var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };
            graphics.DrawString(text, font, textBrush, new RectangleF(0, 0, size, size), format);
        }

        return bitmap.GetHicon();
    }

    /// <summary>托盘圆点图标。返回的 HICON 需调用方销毁。</summary>
    public static IntPtr CreateDot(Color accent)
    {
        var size = 32;
        using var bitmap = new Bitmap(size, size);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);

            using var fill = new SolidBrush(accent);
            graphics.FillEllipse(fill, 5, 5, size - 10, size - 10);

            using var inner = new SolidBrush(ContrastColor(accent));
            graphics.FillRectangle(inner, 13, 8, 6, 10);
            graphics.FillEllipse(inner, 13, 20, 6, 4);
        }

        return bitmap.GetHicon();
    }

    public static void Destroy(IntPtr icon)
    {
        if (icon != IntPtr.Zero)
        {
            NativeMethods.DestroyIcon(icon);
        }
    }

    private static Color ContrastColor(Color background)
        => (background.R * 299 + background.G * 587 + background.B * 114) / 1000 > 140
            ? Color.FromArgb(255, 32, 32, 32)
            : Color.FromArgb(255, 255, 255, 255);

    /// <summary>系统强调色；取不到时退回系统高亮色（不写死色值）。</summary>
    public static Color AccentColor()
    {
        try
        {
            // 命名空间叫 Cirno.Shell.Windows 时，裸写 Windows.* 会解析到本命名空间
            var ui = new global::Windows.UI.ViewManagement.UISettings();
            var color = ui.GetColorValue(global::Windows.UI.ViewManagement.UIColorType.Accent);
            return Color.FromArgb(255, color.R, color.G, color.B);
        }
        catch
        {
            return SystemColors.Highlight;
        }
    }
}
