using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace PaperTodo.Plugin.DataIo;

/// <summary>
/// 解析本插件导出的 Markdown（也兼容常见的手写 Markdown）：
/// 以 &lt;!-- papertodo-dataio:v2 type=... --&gt; 标记分节，同时兼容旧版
/// &lt;!-- papertodo:type=... --&gt;；没有标记时整份文件视为一张纸片。
/// 待办识别 - [ ] / - [x] 列表，其余内容识别为笔记正文。
/// 代码块内出现的标记会被忽略，笔记正文中的同名标记在导出时会被转义。
/// </summary>
internal static partial class MarkdownImportParser
{
    /// <summary>
    /// 解析前允许的最大行数。64 MiB 文件若全是换行会被 Split 拆成数千万个字符串，
    /// 单个文件就能占用近 1 GiB 内存；正常导入（200 张、单张 10 万字符）远达不到该上限。
    /// </summary>
    internal const int MaxMarkdownLines = 2_000_000;

    /// <summary>正文不可读取的笔记使用的标记，避免把占位文案当作真正文导入。</summary>
    internal const string ContentUnavailableMarker = "papertodo-dataio:content-unavailable";

    /// <summary>标题为空的纸片使用的标记；没有它时导入端会用文件名兜底标题。</summary>
    internal const string EmptyTitleMarker = "papertodo-dataio:title-empty";

    /// <summary>正文在导出时被截断的标记；带此标记的纸片不能用于更新。</summary>
    internal const string ContentTruncatedMarker = "papertodo-dataio:content-truncated";

    /// <summary>待办在导出时被截断（数量或单条长度超限）的标记；带此标记的纸片不能用于更新。</summary>
    internal const string TodosTruncatedMarker = "papertodo-dataio:todos-truncated";

    /// <summary>
    /// 导出时为未闭合代码围栏补的闭合标记；导入时据此移除人工补上的围栏行，
    /// 保持正文与导出前一致，同时避免围栏吞掉下一张纸片。
    /// </summary>
    internal const string FenceClosedMarker = "papertodo-dataio:fence-closed";

    /// <summary>旧版导出使用的占位文案；仅当正文整体等于该文案时才按“正文缺失”处理。</summary>
    private const string LegacyUnavailablePlaceholder = "（正文不可读取，未导出）";

    [GeneratedRegex(
        @"^<!--\s*papertodo(?:-dataio:v2)?[: ]\s*type=(todo|note)(?:\s+sourceId=(\S+))?\s*-->\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TypeMarkerPattern();

    [GeneratedRegex(
        @"^[-*+]\s+\[([ xX])\]\s?(.*)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex CheckboxPattern();

    public static ExportDocument Parse(string markdown, string filePath)
    {
        // 在 Split 之前限制行数：否则畸形文件会在分配阶段耗尽内存。
        var lineCount = CountLines(markdown);
        if (lineCount > MaxMarkdownLines)
        {
            throw new ImportFileLimitException(
                $"Markdown 文件包含 {lineCount} 行，超过 {MaxMarkdownLines} 行的解析上限；" +
                "请拆分后再导入。");
        }

        var lines = MarkdownNormalize.NormalizeLineEndings(markdown).Split('\n');

        var markers = new List<(int Line, string Type, string SourceId, bool V2)>();
        var fenceCharacter = '\0';
        for (var index = 0; index < lines.Length; index++)
        {
            var trimmed = lines[index].Trim();

            // 代码块内的注释标记只是笔记内容，不能用来分节。
            if (TryToggleFence(trimmed, ref fenceCharacter))
            {
                continue;
            }

            if (fenceCharacter != '\0')
            {
                // 未闭合的围栏会让后续所有纸片都被当成代码内容。
                // 导出端会转义正文中的标记行，并给未闭合的围栏补一个闭合围栏，
                // 因此插件生成的文件不会出现“围栏内未被转义的 v2 标记”；
                // 这里仍保持保守：只有围栏之后确实没有闭合围栏时才当作分节边界。
                if (TypeMarkerPattern().IsMatch(trimmed) &&
                    trimmed.Contains("-dataio:v2", StringComparison.OrdinalIgnoreCase) &&
                    !HasClosingFenceAhead(lines, index + 1, fenceCharacter))
                {
                    fenceCharacter = '\0';
                }
                else
                {
                    continue;
                }
            }

            var match = TypeMarkerPattern().Match(trimmed);
            if (match.Success)
            {
                markers.Add((
                    index,
                    match.Groups[1].Value.ToLowerInvariant(),
                    match.Groups[2].Success ? match.Groups[2].Value : "",
                    trimmed.Contains("-dataio:v2", StringComparison.OrdinalIgnoreCase)));
            }
        }

        // 分节标记已全部扫出：在构建纸片对象之前拒绝超限文件，
        // 避免为几十万张必然被导入端跳过的纸片分配对象。
        if (markers.Count > ImportPlanParser.MaxPapers)
        {
            throw new ImportFileLimitException(
                $"Markdown 文件包含 {markers.Count} 张纸片，超过单次导入上限 " +
                $"{ImportPlanParser.MaxPapers} 张；请拆分后再导入。");
        }

        var papers = new List<ExportPaper>();
        if (markers.Count == 0)
        {
            // 没有分节标记的普通 Markdown：绝不按旧版占位文案改写正文。
            papers.Add(ParseSection(
                lines, 0, lines.Length, null, "", filePath, papers.Count, v2Format: true));
        }
        else
        {
            for (var index = 0; index < markers.Count; index++)
            {
                var start = markers[index].Line + 1;
                var end = index + 1 < markers.Count ? markers[index + 1].Line : lines.Length;
                papers.Add(ParseSection(
                    lines,
                    start,
                    end,
                    markers[index].Type,
                    markers[index].SourceId,
                    filePath,
                    papers.Count,
                    markers[index].V2));
            }
        }

        return new ExportDocument
        {
            ExportedAt = DateTimeOffset.Now,
            Source = new ExportSource
            {
                PluginId = "papertodo.dataio.native",
                PluginVersion = typeof(MarkdownImportParser).Assembly.GetName().Version?.ToString(3) ?? "1.0.0",
                ApiVersion = "",
                UiLanguage = "",
                Note = "从 Markdown 文件导入；不包含提醒、关联纸片与可见性等数据。"
            },
            Papers = papers
        };
    }

    private static ExportPaper ParseSection(
        string[] lines,
        int start,
        int end,
        string? markedType,
        string sourceId,
        string filePath,
        int paperIndex,
        bool v2Format)
    {
        var bodyStart = start;
        string? title = null;
        var contentTruncated = false;
        var todosTruncated = false;

        // 导出端写在标题前的结构标记：空标题、正文/待办截断。
        while (bodyStart < end && string.IsNullOrWhiteSpace(lines[bodyStart]))
        {
            bodyStart++;
        }

        while (bodyStart < end)
        {
            if (IsEmptyTitleMarker(lines[bodyStart]))
            {
                // 明确标记的空标题：保留为空，而不是用文件名兜底。
                title = "";
            }
            else if (IsContentTruncatedMarker(lines[bodyStart]))
            {
                contentTruncated = true;
            }
            else if (IsTodosTruncatedMarker(lines[bodyStart]))
            {
                todosTruncated = true;
            }
            else
            {
                break;
            }

            bodyStart++;
        }

        if (title is null &&
            bodyStart < end &&
            lines[bodyStart].StartsWith("# ", StringComparison.Ordinal))
        {
            title = lines[bodyStart][2..].Trim();
            bodyStart++;
        }

        // 导出格式在标题后固定空一行作为结构分隔；只跳过这一行，
        // 正文自身以空行开头时（文件里会有两个空行）仍保留用户内容。
        if (title is not null && bodyStart < end && lines[bodyStart].Length == 0)
        {
            bodyStart++;
        }

        var type = markedType ?? InferType(lines, bodyStart, end);
        var paper = new ExportPaper
        {
            // 只有带来源标记的 Markdown 才参与后续“更新原纸片”的身份匹配。
            SourceId = sourceId,
            Type = type,
            // 文件里没有标题行时用文件名兜底；显式空标题标记保持为空。
            Title = title ?? FallbackTitle(filePath, paperIndex),
            Show = true,
            ContentTruncated = contentTruncated,
            TodosTruncated = todosTruncated
        };

        if (type == "todo")
        {
            paper.Todos = ParseTodos(lines, bodyStart, end);
        }
        else
        {
            ParseNoteBody(paper, lines, bodyStart, end, v2Format);
        }

        return paper;
    }

    /// <summary>
    /// 解析笔记正文：识别“正文不可读取”标记，并还原导出时为躲避分节扫描而加的转义。
    /// </summary>
    private static void ParseNoteBody(
        ExportPaper paper,
        string[] lines,
        int start,
        int end,
        bool v2Format)
    {
        var bodyLines = new List<string>(Math.Max(0, end - start));
        var unavailable = false;
        for (var index = start; index < end; index++)
        {
            var line = lines[index];
            if (IsContentUnavailableMarker(line))
            {
                unavailable = true;
                continue;
            }

            if (IsFenceClosedMarker(line))
            {
                // 移除导出时补上的闭合围栏，恢复正文原本的未闭合状态。
                if (bodyLines.Count > 0 && IsFenceLine(bodyLines[^1]))
                {
                    bodyLines.RemoveAt(bodyLines.Count - 1);
                }

                continue;
            }

            bodyLines.Add(UnescapeNoteMarkerLine(line));
        }

        // 不可读的笔记不保留占位文案，导入后按“正文缺失”处理，避免把提示写进真正的笔记。
        // 不额外 Trim：正文首尾的空行与行尾空白都是用户内容的一部分，
        // 只由 TrimTrailingBlankAndSeparator 去掉往返结构（末尾空行与分节分隔线）。
        var content = unavailable
            ? ""
            : MarkdownNormalize.TrimTrailingBlankAndSeparator(string.Join("\n", bodyLines));

        // 兼容旧版导出：旧格式没有独立标记，正文只有一条占位文案。
        // v2 文件与普通 Markdown 的正文可能恰好就是这句话，不能据此清空真正的笔记。
        if (!unavailable &&
            !v2Format &&
            string.Equals(content, LegacyUnavailablePlaceholder, StringComparison.Ordinal))
        {
            unavailable = true;
            content = "";
        }

        paper.Content = content;
        paper.ContentAvailable = !unavailable;
    }

    private static string InferType(string[] lines, int start, int end)
    {
        var sawCheckbox = false;
        var afterCheckbox = false;
        for (var index = start; index < end; index++)
        {
            var line = lines[index];
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (CheckboxPattern().IsMatch(line))
            {
                sawCheckbox = true;
                afterCheckbox = true;
                continue;
            }

            if (afterCheckbox && IsContinuation(line))
            {
                continue;
            }

            // 出现待办列表以外的内容时按笔记处理，避免误吞正文。
            return "note";
        }

        return sawCheckbox ? "todo" : "note";
    }

    private static List<ExportTodo> ParseTodos(string[] lines, int start, int end)
    {
        var todos = new List<ExportTodo>();
        ExportTodo? current = null;

        // 续行必须累积到 StringBuilder：`text += ...` 每读一行都复制整段已累积文本，
        // 上千万行续行会退化为 O(n²)（实测 20 万行约 7 秒），且发生在长度上限检查之前。
        StringBuilder? currentText = null;

        void Flush()
        {
            if (current is not null && currentText is not null)
            {
                current.Text = currentText.ToString();
            }
        }

        for (var index = start; index < end; index++)
        {
            var line = lines[index];
            var match = CheckboxPattern().Match(line);
            if (match.Success)
            {
                Flush();
                current = new ExportTodo
                {
                    Text = Unescape(match.Groups[2].Value),
                    Done = match.Groups[1].Value is "x" or "X",
                    Order = todos.Count
                };
                currentText = null;
                todos.Add(current);
                continue;
            }

            // 续行缩进 4 个空格（或 1 个 Tab），与 PaperTodo 原生导出保持一致。
            if (current != null && IsContinuation(line))
            {
                currentText ??= new StringBuilder(current.Text);
                currentText
                    .Append('\n')
                    .Append(Unescape(line[line[0] == '\t' ? 1.. : 4..]));
            }
        }

        Flush();
        return todos;
    }

    /// <summary>统计换行数量；不分配任何字符串数组。</summary>
    internal static int CountLines(string text)
    {
        var lines = 1;
        foreach (var character in text)
        {
            if (character == '\n')
            {
                lines++;
            }
        }

        return lines;
    }

    private static bool IsContinuation(string line) =>
        line.StartsWith("\t", StringComparison.Ordinal) ||
        line.StartsWith("    ", StringComparison.Ordinal);

    /// <summary>判断一行是否为本插件的分节标记（用于导出时转义笔记正文中的同名行）。</summary>
    internal static bool IsSectionMarkerLine(string line) =>
        TypeMarkerPattern().IsMatch(line.Trim());

    /// <summary>判断一行是否是“正文不可读取”标记。</summary>
    internal static bool IsContentUnavailableMarker(string line)
    {
        var trimmed = line.Trim();
        return trimmed.StartsWith("<!--", StringComparison.Ordinal) &&
               trimmed.EndsWith("-->", StringComparison.Ordinal) &&
               trimmed.Contains(ContentUnavailableMarker, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>判断一行是否是“空标题”标记。</summary>
    internal static bool IsEmptyTitleMarker(string line)
    {
        var trimmed = line.Trim();
        return trimmed.StartsWith("<!--", StringComparison.Ordinal) &&
               trimmed.EndsWith("-->", StringComparison.Ordinal) &&
               trimmed.Contains(EmptyTitleMarker, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>判断一行是否是“补闭合围栏”标记。</summary>
    internal static bool IsFenceClosedMarker(string line)
    {
        var trimmed = line.Trim();
        return trimmed.StartsWith("<!--", StringComparison.Ordinal) &&
               trimmed.EndsWith("-->", StringComparison.Ordinal) &&
               trimmed.Contains(FenceClosedMarker, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>判断一行是否是“正文被截断”标记。</summary>
    internal static bool IsContentTruncatedMarker(string line) =>
        IsMarkerLine(line, ContentTruncatedMarker);

    /// <summary>判断一行是否是“待办被截断”标记。</summary>
    internal static bool IsTodosTruncatedMarker(string line) =>
        IsMarkerLine(line, TodosTruncatedMarker);

    private static bool IsMarkerLine(string line, string marker)
    {
        var trimmed = line.Trim();
        return trimmed.StartsWith("<!--", StringComparison.Ordinal) &&
               trimmed.EndsWith("-->", StringComparison.Ordinal) &&
               trimmed.Contains(marker, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFenceLine(string line)
    {
        var trimmed = line.Trim();
        return trimmed.StartsWith("```", StringComparison.Ordinal) ||
               trimmed.StartsWith("~~~", StringComparison.Ordinal);
    }

    /// <summary>
    /// 围栏之后是否还有同种闭合围栏。遇到下一个 v2 分节标记即停止：
    /// 说明当前围栏在分节边界仍未闭合，应把它当作未闭合围栏处理。
    /// </summary>
    private static bool HasClosingFenceAhead(string[] lines, int start, char fenceCharacter)
    {
        var fence = new string(fenceCharacter, 3);
        for (var index = start; index < lines.Length; index++)
        {
            var trimmed = lines[index].Trim();
            if (trimmed.StartsWith(fence, StringComparison.Ordinal))
            {
                return true;
            }

            if (TypeMarkerPattern().IsMatch(trimmed) &&
                trimmed.Contains("-dataio:v2", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// 代码块围栏：``` 与 ~~~ 只与同种围栏成对切换。
    /// 在 ~~~ 块内出现 ``` 行时不会误把围栏类型切换掉。
    /// 返回 true 表示当前行是围栏行。
    /// </summary>
    internal static bool TryToggleFence(string trimmed, ref char fenceCharacter)
    {
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            if (fenceCharacter == '\0')
            {
                fenceCharacter = '`';
            }
            else if (fenceCharacter == '`')
            {
                fenceCharacter = '\0';
            }

            return true;
        }

        if (trimmed.StartsWith("~~~", StringComparison.Ordinal))
        {
            if (fenceCharacter == '\0')
            {
                fenceCharacter = '~';
            }
            else if (fenceCharacter == '~')
            {
                fenceCharacter = '\0';
            }

            return true;
        }

        return false;
    }

    /// <summary>行是否为插件结构标记之一（导出端转义判断与导入端还原共用）。</summary>
    internal static bool IsAnyNoteMarkerLine(string line) =>
        IsSectionMarkerLine(line) ||
        IsContentUnavailableMarker(line) ||
        IsEmptyTitleMarker(line) ||
        IsFenceClosedMarker(line) ||
        IsContentTruncatedMarker(line) ||
        IsTodosTruncatedMarker(line);

    /// <summary>去掉导出时加到标记行开头的反斜杠。</summary>
    private static string UnescapeNoteMarkerLine(string line)
    {
        var start = line.Length - line.TrimStart().Length;
        if (start >= line.Length || line[start] != '\\')
        {
            return line;
        }

        var withoutBackslash = line.Remove(start, 1);

        // 两种情况都要剥掉一层反斜杠：
        // 1. 导出端为标记行加的转义：去掉后正好是标记；
        // 2. 用户内容本来就以反斜杠 + 标记行开头：导出端会再加一层，去掉后仍是该内容。
        var trimmed = withoutBackslash.TrimStart();
        var escapedMarker = trimmed.StartsWith('\\') && IsAnyNoteMarkerLine(trimmed[1..]);
        return IsAnyNoteMarkerLine(withoutBackslash) || escapedMarker
            ? withoutBackslash
            : line;
    }

    private static string FallbackTitle(string filePath, int paperIndex)
    {
        var stem = Path.GetFileNameWithoutExtension(filePath);
        if (string.IsNullOrWhiteSpace(stem))
        {
            stem = "Markdown 导入";
        }

        return paperIndex == 0 ? stem : $"{stem}（{paperIndex + 1}）";
    }

    /// <summary>还原导出时对 Markdown 结构字符的转义。</summary>
    private static string Unescape(string text)
    {
        const string escapable = "\\`*_{}[]<>~|&#+-.)";
        var result = new System.Text.StringBuilder(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '\\' &&
                index + 1 < text.Length &&
                escapable.Contains(text[index + 1]))
            {
                result.Append(text[index + 1]);
                index++;
                continue;
            }

            result.Append(text[index]);
        }

        return result.ToString();
    }
}
