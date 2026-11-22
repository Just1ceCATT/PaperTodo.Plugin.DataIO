namespace PaperTodo.Plugin.DataIo;

/// <summary>
/// Markdown 正文规范化工具：统一换行、判断分隔线、比较“往返等价”的正文。
/// 导入与更新路径共用，避免两处实现出现分歧。
/// </summary>
internal static class MarkdownNormalize
{
    public static string NormalizeLineEndings(string? text) =>
        (text ?? "")
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

    /// <summary>三个及以上连续短横线的行，视为 Markdown 分隔线。</summary>
    public static bool IsSeparatorLine(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Length >= 3 && trimmed.All(character => character == '-');
    }

    /// <summary>去掉行尾空行与末尾分隔线；Markdown 往返不会保留这些结构。</summary>
    public static string TrimTrailingBlankAndSeparator(string? text)
    {
        var lines = NormalizeLineEndings(text).Split('\n').ToList();
        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1]))
        {
            lines.RemoveAt(lines.Count - 1);
        }

        if (lines.Count > 0 && IsSeparatorLine(lines[^1]))
        {
            lines.RemoveAt(lines.Count - 1);
            while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1]))
            {
                lines.RemoveAt(lines.Count - 1);
            }
        }

        return string.Join("\n", lines);
    }

    /// <summary>忽略行尾空行与末尾分隔线后比较两段正文是否等价。</summary>
    public static bool ContentEquivalent(string? left, string? right) =>
        string.Equals(
            TrimTrailingBlankAndSeparator(left),
            TrimTrailingBlankAndSeparator(right),
            StringComparison.Ordinal);
}
