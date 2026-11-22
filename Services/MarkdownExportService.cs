using System.Text;

namespace PaperTodo.Plugin.DataIo;

/// <summary>
/// 把导出文档写成与宿主原生导出一致的 Markdown：
/// 待办为 - [ ] / - [x] 列表，笔记保留原始 Markdown，多张纸片按标题分节。
/// Markdown 不包含提醒、关联纸片、可见性等 PaperTodo 专有数据。
/// </summary>
internal static class MarkdownExportService
{
    /// <summary>旧版导出使用的图片缺失标记；仍用于识别旧文件，避免更新时破坏图片引用。</summary>
    public const string MissingImageLabel = "图片缺失";
    private const string AlwaysEscapedMarkdownCharacters = "\\`*_{}[]<>~|&";

    public static void Write(string path, ExportDocument document)
    {
        var builder = new StringBuilder();
        for (var index = 0; index < document.Papers.Count; index++)
        {
            var paper = document.Papers[index];
            if (index > 0)
            {
                builder.AppendLine();
                builder.AppendLine("---");
                builder.AppendLine();
            }

            var isTodo = string.Equals(paper.Type, "todo", StringComparison.OrdinalIgnoreCase);

            // 不可见的 HTML 注释标记纸片类型与来源 ID，供本插件重新导入时区分类型并更新原纸片。
            builder.Append("<!-- papertodo:type=")
                .Append(isTodo ? "todo" : "note");
            if (!string.IsNullOrWhiteSpace(paper.SourceId) &&
                paper.SourceId.All(character => !char.IsWhiteSpace(character)))
            {
                builder.Append(" sourceId=").Append(paper.SourceId);
            }

            builder.AppendLine(" -->");
            AppendTitle(builder, paper.Title);
            if (isTodo)
            {
                AppendTodos(builder, paper);
            }
            else
            {
                AppendNote(builder, paper);
            }
        }

        JsonFiles.WriteAllTextAtomic(path, builder.ToString());
    }

    private static void AppendTitle(StringBuilder builder, string? title)
    {
        var text = string.IsNullOrWhiteSpace(title)
            ? "（无标题）"
            : title.ReplaceLineEndings(" ").Trim();
        builder.Append("# ").AppendLine(text).AppendLine();
    }

    private static void AppendTodos(StringBuilder builder, ExportPaper paper)
    {
        var todos = paper.Todos?
            .Where(todo => !string.IsNullOrWhiteSpace(todo.Text))
            .OrderBy(todo => todo.Order)
            .ToArray() ?? [];

        if (todos.Length == 0)
        {
            builder.AppendLine("（暂无待办）");
            return;
        }

        for (var index = 0; index < todos.Length; index++)
        {
            var todo = todos[index];
            builder.Append(todo.Done ? "- [x] " : "- [ ] ");

            // 与宿主剪贴板/原生导出保持一致：多行待办用 4 个空格续行。
            var lines = todo.Text
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .Split('\n');
            for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
            {
                if (lineIndex > 0)
                {
                    builder.AppendLine();
                    builder.Append("    ");
                }

                AppendEscapedLine(builder, lines[lineIndex]);
            }

            builder.AppendLine();
        }
    }

    private static void AppendNote(StringBuilder builder, ExportPaper paper)
    {
        if (paper.ContentAvailable == false || string.IsNullOrEmpty(paper.Content))
        {
            builder.AppendLine("（正文不可读取，未导出）");
            return;
        }

        // 图片引用原样保留：图片本体虽然不在文件中，但引用文本必须完整写入，
        // 否则把导出的 Markdown 再导入（更新同一纸片）时会用“图片缺失”标记覆盖原正文。
        builder.AppendLine(paper.Content);
    }

    /// <summary>按宿主原生导出规则转义会改变 Markdown 结构的字符。</summary>
    private static void AppendEscapedLine(StringBuilder builder, string line)
    {
        var firstContent = 0;
        while (firstContent < line.Length && char.IsWhiteSpace(line[firstContent]))
        {
            firstContent++;
        }

        var orderedDelimiter = OrderedListDelimiterIndex(line, firstContent);
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            var escapeBlockMarker =
                index == firstContent &&
                character is '#' or '>' or '+' or '-';
            var escapeOrderedDelimiter = index == orderedDelimiter;
            if (AlwaysEscapedMarkdownCharacters.Contains(character) ||
                escapeBlockMarker ||
                escapeOrderedDelimiter)
            {
                builder.Append('\\');
            }

            builder.Append(character);
        }
    }

    private static int OrderedListDelimiterIndex(string line, int firstContent)
    {
        if (firstContent >= line.Length || !char.IsDigit(line[firstContent]))
        {
            return -1;
        }

        var index = firstContent;
        while (index < line.Length && char.IsDigit(line[index]))
        {
            index++;
        }

        if (index >= line.Length || line[index] is not ('.' or ')'))
        {
            return -1;
        }

        var after = index + 1;
        return after >= line.Length || char.IsWhiteSpace(line[after]) ? index : -1;
    }
}
