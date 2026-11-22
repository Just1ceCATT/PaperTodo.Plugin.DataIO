namespace PaperTodo.Plugin.DataIo;

/// <summary>
/// 一行差异：' ' 表示相同，'-' 表示本地有而文件没有，'+' 表示文件新增。
/// OldLine/NewLine 为 1 基行号，0 表示该侧没有对应行。
/// </summary>
internal readonly record struct DiffLine(char Kind, string Text, int OldLine, int NewLine);

/// <summary>
/// 轻量行级差异：保留共同前缀/后缀附近的少量上下文，中间分别列出删除和新增行。
/// 对超长内容做截断，避免预览窗口消耗过多内存。
/// </summary>
internal static class TextDiff
{
    private const int ContextLines = 2;
    private const int MaxLines = 160;

    public static IReadOnlyList<DiffLine> Compute(string? before, string? after)
    {
        var oldLines = Split(before);
        var newLines = Split(after);

        var prefix = 0;
        while (prefix < oldLines.Length &&
               prefix < newLines.Length &&
               string.Equals(oldLines[prefix], newLines[prefix], StringComparison.Ordinal))
        {
            prefix++;
        }

        var suffix = 0;
        while (suffix < oldLines.Length - prefix &&
               suffix < newLines.Length - prefix &&
               string.Equals(oldLines[^(suffix + 1)], newLines[^(suffix + 1)], StringComparison.Ordinal))
        {
            suffix++;
        }

        var result = new List<DiffLine>();
        var prefixStart = Math.Max(0, prefix - ContextLines);
        for (var i = prefixStart; i < prefix; i++)
        {
            result.Add(new DiffLine(' ', oldLines[i], i + 1, i + 1));
        }

        for (var i = prefix; i < oldLines.Length - suffix; i++)
        {
            result.Add(new DiffLine('-', oldLines[i], i + 1, 0));
        }

        for (var i = prefix; i < newLines.Length - suffix; i++)
        {
            result.Add(new DiffLine('+', newLines[i], 0, i + 1));
        }

        var suffixShown = Math.Min(suffix, ContextLines);
        for (var i = oldLines.Length - suffixShown; i < oldLines.Length; i++)
        {
            var offset = i - (oldLines.Length - suffixShown);
            result.Add(new DiffLine(
                ' ',
                oldLines[i],
                i + 1,
                newLines.Length - suffixShown + offset + 1));
        }

        if (result.Count > MaxLines)
        {
            result = result.Take(MaxLines).ToList();
            result.Add(new DiffLine(' ', "…（差异已截断）", 0, 0));
        }

        return result;
    }

    public static string TodoSnapshot(IEnumerable<(string Text, bool Done)> items)
    {
        return string.Join(
            "\n",
            items.Select(item => (item.Done ? "- [x] " : "- [ ] ") + item.Text));
    }

    private static string[] Split(string? text) =>
        string.IsNullOrEmpty(text)
            ? []
            : text.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .Split('\n');
}
