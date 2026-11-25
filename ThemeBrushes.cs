using System.Windows.Media;

namespace PaperTodo.Plugin.DataIo;

/// <summary>
/// 把宿主提供的十六进制主题色解析为画刷。正文与悬停面板共用同一套回退色，
/// 解析失败时使用固定色，绝不因为主题值异常而让界面不可见。
/// </summary>
internal static class ThemeBrushes
{
    public static SolidColorBrush MakeBrush(string? hex, Color fallback)
    {
        var brush = new SolidColorBrush(ParseColor(hex, fallback));
        brush.Freeze();
        return brush;
    }

    public static SolidColorBrush MakeBrushWithAlpha(string? hex, byte alpha, Color fallback)
    {
        var color = ParseColor(hex, fallback);
        var brush = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// 归一化宿主下发的字体缩放。NaN/Infinity/非正数会让所有字号、边距变成无效值，
    /// 进而让 WPF 布局反复失败、整个插件正文显示为空白，因此必须回退到 1.0。
    /// </summary>
    public static double NormalizeScale(double scale, double minimum, double maximum) =>
        double.IsFinite(scale) && scale > 0
            ? Math.Clamp(scale, minimum, maximum)
            : 1.0;

    public static Color ParseColor(string? hex, Color fallback)
    {
        if (!string.IsNullOrWhiteSpace(hex))
        {
            try
            {
                if (ColorConverter.ConvertFromString(hex) is Color color)
                {
                    return color;
                }
            }
            catch (FormatException)
            {
                // 主题值异常时回退到固定色。
            }
            catch (NotSupportedException)
            {
                // 与 FormatException 相同，保持界面可用。
            }
        }

        return fallback;
    }
}
