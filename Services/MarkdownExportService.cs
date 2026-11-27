using System.Text;

namespace PaperTodo.Plugin.DataIo;

/// <summary>
/// 把导出文档写成与 PaperTodo 原生导出一致的 Markdown：
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
        var markdown = Build(document);
        JsonFiles.EnsureFitsImportLimit(markdown, "导出 Markdown");
        JsonFiles.WriteAllTextAtomic(path, markdown);
    }

    /// <summary>
    /// 生成 Markdown 文本。imageLinkResolver 非空时，把 (纸片来源 ID, 图片 ID)
    /// 解析为压缩包内的相对链接；返回 null 表示该图片未随包导出，引用保持原样。
    /// </summary>
    public static string Build(
        ExportDocument document,
        Func<string, string, string?>? imageLinkResolver = null)
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
            // v2 标记带独立前缀，避免与笔记正文里普通的 papertodo 注释混淆；导入端仍兼容旧格式。
            builder.Append("<!-- papertodo-dataio:v2 type=")
                .Append(isTodo ? "todo" : "note");
            if (!string.IsNullOrWhiteSpace(paper.SourceId) &&
                paper.SourceId.All(character => !char.IsWhiteSpace(character)))
            {
                builder.Append(" sourceId=").Append(paper.SourceId);
            }

            builder.AppendLine(" -->");

            // 截断标记必须写进 Markdown：否则回导更新时会用截断后的正文覆盖本地，
            // 或在开启“删除文件外待办”时删掉被截掉的条目。
            if (paper.ContentTruncated)
            {
                builder.Append("<!-- ")
                    .Append(MarkdownImportParser.ContentTruncatedMarker)
                    .AppendLine(" -->");
            }

            if (paper.TodosTruncated)
            {
                builder.Append("<!-- ")
                    .Append(MarkdownImportParser.TodosTruncatedMarker)
                    .AppendLine(" -->");
            }

            AppendTitle(builder, paper.Title);
            if (isTodo)
            {
                AppendTodos(builder, paper);
            }
            else
            {
                AppendNote(builder, paper, imageLinkResolver);
            }
        }

        var markdown = builder.ToString();

        // 导入端按行数限额，导出端也必须遵守：否则 200 篇短行正文可能生成
        // 字节数未超限、行数却超过 200 万的文件，直到需要恢复时才发现无法导入。
        if (MarkdownImportParser.CountLines(markdown) > MarkdownImportParser.MaxMarkdownLines)
        {
            throw new InvalidOperationException(
                $"导出 Markdown 超过 {MarkdownImportParser.MaxMarkdownLines} 行的导入上限，" +
                "生成的文件将无法被本插件重新导入；请减少纸片数量或改用 JSON 导出。");
        }

        return markdown;
    }

    private static void AppendTitle(StringBuilder builder, string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            // 用独立标记表达“标题为空”，避免回导时被文件名兜底成“（无标题）”或文件名。
            builder.Append("<!-- ").Append(MarkdownImportParser.EmptyTitleMarker).AppendLine(" -->");
            builder.AppendLine();
            return;
        }

        builder.Append("# ").AppendLine(title.ReplaceLineEndings(" ").Trim()).AppendLine();
    }

    private static void AppendTodos(StringBuilder builder, ExportPaper paper)
    {
        // 空白待办仍原样导出（与 JSON 一致）。 PaperTodo 拒绝创建空文本待办，
        // 因此导入端会丢弃它们；这类占位条目无法在往返后保留。
        var todos = paper.Todos?
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

            // 与 PaperTodo 剪贴板/原生导出保持一致：多行待办用 4 个空格续行。
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

    private static void AppendNote(
        StringBuilder builder,
        ExportPaper paper,
        Func<string, string, string?>? imageLinkResolver)
    {
        if (paper.ContentAvailable == false)
        {
            // 用独立标记表达“正文不可读取”，导入端据此保留 ContentAvailable=false，
            // 不会把下面的占位文案当作真正的笔记正文写回 PaperTodo。
            builder.Append("<!-- ").Append(MarkdownImportParser.ContentUnavailableMarker).AppendLine(" -->");
            builder.AppendLine("（正文不可读取，未导出）");
            return;
        }

        // 无图片包时引用原样保留；含图片包时改写成压缩包内的相对链接。
        // 两种写法都必须完整写入，否则回导更新同一纸片时会破坏本地图片引用。
        var content = paper.Content ?? "";
        if (imageLinkResolver != null)
        {
            content = ImageReferenceScanner.RewriteReferences(
                content,
                imageId => imageLinkResolver(paper.SourceId, imageId));
        }

        AppendNoteContent(builder, content);
    }

    /// <summary>
    /// 原样写入笔记正文；若某行恰好是本插件的标记行，在行内插一个反斜杠转义，
    /// 避免回导时被误认为分节或“正文不可读取”标记。导入端会还原该反斜杠。
    /// </summary>
    private static void AppendNoteContent(StringBuilder builder, string content)
    {
        var fenceCharacter = '\0';
        foreach (var rawLine in MarkdownNormalize.NormalizeLineEndings(content).Split('\n'))
        {
            var line = rawLine;
            if (MarkdownImportParser.IsAnyNoteMarkerLine(line))
            {
                var markerStart = line.IndexOf("<!--", StringComparison.Ordinal);
                if (markerStart >= 0)
                {
                    line = line[..markerStart] + "\\" + line[markerStart..];
                }
            }
            else
            {
                // 用户正文本来就写成 "\<!-- 标记 -->" 时导出端不会再转义，
                // 导入端却会剥掉反斜杠；这里补一个反斜杠保证往返可逆。
                var trimmed = line.TrimStart();
                var indent = line.Length - trimmed.Length;
                if (trimmed.StartsWith('\\') &&
                    MarkdownImportParser.IsAnyNoteMarkerLine(trimmed[1..]))
                {
                    line = line[..indent] + "\\" + line[indent..];
                }
            }

            MarkdownImportParser.TryToggleFence(line.Trim(), ref fenceCharacter);
            builder.AppendLine(line);
        }

        // 正文以未闭合的代码围栏结束时，主动补一个闭合围栏；
        // 否则回导时围栏会一直开着，把下一张纸片的正文吞进去。
        if (fenceCharacter != '\0')
        {
            builder.AppendLine(fenceCharacter == '~' ? "~~~" : "```");
            builder.Append("<!-- ").Append(MarkdownImportParser.FenceClosedMarker).AppendLine(" -->");
        }
    }

    /// <summary>按 PaperTodo 原生导出规则转义会改变 Markdown 结构的字符。</summary>
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
