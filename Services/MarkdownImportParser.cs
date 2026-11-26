using System.IO;
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
    /// <summary>正文不可读取的笔记使用的标记，避免把占位文案当作真正文导入。</summary>
    internal const string ContentUnavailableMarker = "papertodo-dataio:content-unavailable";

    /// <summary>标题为空的纸片使用的标记；没有它时导入端会用文件名兜底标题。</summary>
    internal const string EmptyTitleMarker = "papertodo-dataio:title-empty";

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
        var lines = MarkdownNormalize.NormalizeLineEndings(markdown).Split('\n');

        var markers = new List<(int Line, string Type, string SourceId)>();
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
                    match.Groups[2].Success ? match.Groups[2].Value : ""));
            }
        }

        var papers = new List<ExportPaper>();
        if (markers.Count == 0)
        {
            papers.Add(ParseSection(lines, 0, lines.Length, null, "", filePath, papers.Count));
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
                    papers.Count));
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
        int paperIndex)
    {
        var bodyStart = start;
        string? title = null;
        while (bodyStart < end && string.IsNullOrWhiteSpace(lines[bodyStart]))
        {
            bodyStart++;
        }

        if (bodyStart < end && IsEmptyTitleMarker(lines[bodyStart]))
        {
            // 明确标记的空标题：保留为空，而不是用文件名兜底。
            title = "";
            bodyStart++;
        }
        else if (bodyStart < end && lines[bodyStart].StartsWith("# ", StringComparison.Ordinal))
        {
            title = lines[bodyStart][2..].Trim();
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
            Show = true
        };

        if (type == "todo")
        {
            paper.Todos = ParseTodos(lines, bodyStart, end);
        }
        else
        {
            ParseNoteBody(paper, lines, bodyStart, end);
        }

        return paper;
    }

    /// <summary>
    /// 解析笔记正文：识别“正文不可读取”标记，并还原导出时为躲避分节扫描而加的转义。
    /// </summary>
    private static void ParseNoteBody(ExportPaper paper, string[] lines, int start, int end)
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
        var content = unavailable
            ? ""
            : MarkdownNormalize.TrimTrailingBlankAndSeparator(string.Join("\n", bodyLines).Trim('\n').TrimEnd());

        // 兼容旧版导出：当时没有独立标记，正文只有一条占位文案。
        if (!unavailable && string.Equals(content, LegacyUnavailablePlaceholder, StringComparison.Ordinal))
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

        for (var index = start; index < end; index++)
        {
            var line = lines[index];
            var match = CheckboxPattern().Match(line);
            if (match.Success)
            {
                current = new ExportTodo
                {
                    Text = Unescape(match.Groups[2].Value),
                    Done = match.Groups[1].Value is "x" or "X",
                    Order = todos.Count
                };
                todos.Add(current);
                continue;
            }

            // 续行缩进 4 个空格（或 1 个 Tab），与宿主原生导出保持一致。
            if (current != null && IsContinuation(line))
            {
                current.Text += "\n" + Unescape(line[line[0] == '\t' ? 1.. : 4..]);
            }
        }

        return todos;
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

    /// <summary>去掉导出时加到标记行开头的反斜杠。</summary>
    private static string UnescapeNoteMarkerLine(string line)
    {
        var start = line.Length - line.TrimStart().Length;
        if (start >= line.Length || line[start] != '\\')
        {
            return line;
        }

        var withoutBackslash = line.Remove(start, 1);
        return IsSectionMarkerLine(withoutBackslash) || IsContentUnavailableMarker(withoutBackslash)
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
