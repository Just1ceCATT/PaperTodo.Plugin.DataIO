using System.IO;
using System.Text.RegularExpressions;

namespace PaperTodo.Plugin.DataIo;

/// <summary>
/// 解析本插件导出的 Markdown（也兼容常见的手写 Markdown）：
/// 以 &lt;!-- papertodo:type=... --&gt; 标记分节；没有标记时整份文件视为一张纸片。
/// 待办识别 - [ ] / - [x] 列表，其余内容识别为笔记正文。
/// </summary>
internal static partial class MarkdownImportParser
{
    [GeneratedRegex(
        @"^<!--\s*papertodo:type=(todo|note)\s*-->\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TypeMarkerPattern();

    [GeneratedRegex(
        @"^[-*+]\s+\[([ xX])\]\s?(.*)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex CheckboxPattern();

    public static ExportDocument Parse(string markdown, string filePath)
    {
        var lines = markdown
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');

        var markers = new List<(int Line, string Type)>();
        for (var index = 0; index < lines.Length; index++)
        {
            var match = TypeMarkerPattern().Match(lines[index]);
            if (match.Success)
            {
                markers.Add((index, match.Groups[1].Value.ToLowerInvariant()));
            }
        }

        var papers = new List<ExportPaper>();
        if (markers.Count == 0)
        {
            papers.Add(ParseSection(lines, 0, lines.Length, null, filePath, papers.Count));
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
        string filePath,
        int paperIndex)
    {
        var bodyStart = start;
        string? title = null;
        while (bodyStart < end && string.IsNullOrWhiteSpace(lines[bodyStart]))
        {
            bodyStart++;
        }

        if (bodyStart < end && lines[bodyStart].StartsWith("# ", StringComparison.Ordinal))
        {
            title = lines[bodyStart][2..].Trim();
            bodyStart++;
        }

        var type = markedType ?? InferType(lines, bodyStart, end);
        var paper = new ExportPaper
        {
            SourceId = $"md-{paperIndex + 1}",
            Type = type,
            // Markdown 不保存标题时用文件名兜底，避免导入为无标题纸片。
            Title = string.IsNullOrWhiteSpace(title)
                ? FallbackTitle(filePath, paperIndex)
                : title,
            Show = true
        };

        if (type == "todo")
        {
            paper.Todos = ParseTodos(lines, bodyStart, end);
        }
        else
        {
            var content = string.Join("\n", lines[bodyStart..end]);
            content = StripTrailingSeparator(content.Trim('\n').TrimEnd());
            paper.Content = content;
            paper.ContentAvailable = true;
        }

        return paper;
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

    private static string StripTrailingSeparator(string content)
    {
        var lines = content.Split('\n').ToList();
        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1]))
        {
            lines.RemoveAt(lines.Count - 1);
        }

        if (lines.Count > 0 && IsSeparator(lines[^1]))
        {
            lines.RemoveAt(lines.Count - 1);
            while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1]))
            {
                lines.RemoveAt(lines.Count - 1);
            }
        }

        return string.Join("\n", lines);
    }

    private static bool IsSeparator(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Length >= 3 && trimmed.All(character => character == '-');
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
