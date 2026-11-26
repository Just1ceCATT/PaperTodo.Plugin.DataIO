using System.Globalization;
using System.Text.RegularExpressions;

namespace PaperTodo.Plugin.DataIo;

/// <summary>
/// 与 PaperTodo PaperTitles 保持一致的标题处理：去控制字符，按“显示宽度”截断。
/// PaperTodo 以全角字符为单位限制标题，CJK/全角算 2，拉丁/半角算 1。
/// </summary>
internal static class TitleSanitizer
{
    /// <summary>PaperTodo 标题上限的最大可配置值（全角字符数）。</summary>
    public const int MaxConfiguredTitleLength = 20;

    /// <summary>导出/导入时先按 PaperTodo 最大上限预截断，避免明显超长。</summary>
    public const int PreTruncateDisplayWidth = MaxConfiguredTitleLength * 2;

    private static readonly Regex TitleLimitPattern = new(
        @"cannot exceed\s+(\d+)\s+full-width",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string Normalize(string? title)
    {
        var trimmed = (title ?? "").Trim();
        return string.Join("", trimmed.Where(ch => !char.IsControl(ch)));
    }

    public static int DisplayWidth(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var width = 0;
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            width += IsWide((string)enumerator.Current) ? 2 : 1;
        }

        return width;
    }

    /// <summary>按显示宽度截断，且不切断组合字符或 emoji 簇。</summary>
    public static string TruncateByDisplayWidth(string text, int maximumWidth)
    {
        if (maximumWidth <= 0)
        {
            return "";
        }

        var width = 0;
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            width += DisplayWidth((string)elements.Current);
            if (width > maximumWidth)
            {
                return text[..elements.ElementIndex];
            }
        }

        return text;
    }

    /// <summary>从 PaperTodo 的 invalid_params 文案中解析实际标题上限，用于超长后重试。</summary>
    public static bool TryParseTitleLimit(string? message, out int limit)
    {
        limit = 0;
        if (string.IsNullOrEmpty(message))
        {
            return false;
        }

        var match = TitleLimitPattern.Match(message);
        return match.Success &&
               int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out limit) &&
               limit > 0;
    }

    private static bool IsWide(string element)
    {
        if (element.Length == 0)
        {
            return false;
        }

        var c = element[0];
        return element.Contains('️')    // emoji 呈现选择符
            || element.Contains('⃣')    // 组合键帽
            || char.IsHighSurrogate(c)        // 补充平面符号（含 emoji）
            || (c >= 0x231A && c <= 0x231B)
            || (c >= 0x23E9 && c <= 0x23FA)
            || (c >= 0x25FB && c <= 0x27BF)
            || (c >= 0x2B05 && c <= 0x2B07)
            || (c >= 0x2B1B && c <= 0x2B1C)
            || c == '⭐' || c == '⭕'
            || (c >= 0x1100 && c <= 0x115F)   // 谚文字母
            || (c >= 0x2E80 && c <= 0x303E)   // CJK 部首/符号
            || (c >= 0x3041 && c <= 0x33FF)   // 假名/CJK 符号
            || (c >= 0x3400 && c <= 0x4DBF)   // CJK 扩展 A
            || (c >= 0x4E00 && c <= 0x9FFF)   // CJK 统一表意
            || (c >= 0xA000 && c <= 0xA4CF)   // 彝文
            || (c >= 0xAC00 && c <= 0xD7A3)   // 谚文音节
            || (c >= 0xF900 && c <= 0xFAFF)   // CJK 兼容表意
            || (c >= 0xFF00 && c <= 0xFF60)   // 全角形式
            || (c >= 0xFFE0 && c <= 0xFFE6);  // 全角符号
    }
}
