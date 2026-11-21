using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace PaperTodo.Plugin.DataIo;

/// <summary>预览用的配色与排版参数，由预览窗口按宿主主题构建。</summary>
internal sealed record PreviewPalette(
    Brush TextBrush,
    Brush WeakBrush,
    Brush BorderBrush,
    Brush AccentBrush,
    Brush SurfaceBrush,
    double Scale,
    FontFamily FontFamily,
    FontFamily MonoFontFamily);

/// <summary>
/// 轻量 Markdown 预览渲染：只覆盖标题、列表、引用、代码块、分隔线与常见行内样式，
/// 目标是把笔记内容以可读的排版展示出来，不追求完整的 Markdown 规范。
/// </summary>
internal static class MarkdownPreview
{
    /// <summary>单张纸片的渲染上限，避免超长正文让预览窗口长时间卡住。</summary>
    private const int MaxCharacters = 200_000;

    public static void Render(string markdown, Panel host, PreviewPalette palette)
    {
        host.Children.Clear();

        if (string.IsNullOrWhiteSpace(markdown))
        {
            host.Children.Add(new TextBlock
            {
                Text = "（空白笔记）",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12.5 * palette.Scale,
                Foreground = palette.WeakBrush
            });
            return;
        }

        var text = markdown.Replace("\r\n", "\n").Replace('\r', '\n');
        var truncated = text.Length > MaxCharacters;
        if (truncated)
        {
            text = text[..MaxCharacters];
        }

        var lines = text.Split('\n');
        var index = 0;

        while (index < lines.Length)
        {
            var line = lines[index];
            var trimmed = line.Trim();

            if (trimmed.Length == 0)
            {
                index++;
                continue;
            }

            if (IsFence(trimmed, out var fence))
            {
                var code = new List<string>();
                index++;
                while (index < lines.Length && !IsClosingFence(lines[index].Trim(), fence))
                {
                    code.Add(lines[index]);
                    index++;
                }

                if (index < lines.Length)
                {
                    index++;
                }

                host.Children.Add(BuildCodeBlock(string.Join("\n", code), palette));
                continue;
            }

            if (TryHeading(trimmed, out var level, out var headingText))
            {
                host.Children.Add(BuildHeading(headingText, level, palette));
                index++;
                continue;
            }

            if (IsDivider(trimmed))
            {
                host.Children.Add(BuildDivider(palette));
                index++;
                continue;
            }

            if (trimmed.StartsWith('>'))
            {
                var quote = new List<string>();
                while (index < lines.Length && lines[index].TrimStart().StartsWith('>'))
                {
                    var content = lines[index].TrimStart()[1..];
                    if (content.StartsWith(' '))
                    {
                        content = content[1..];
                    }

                    quote.Add(content);
                    index++;
                }

                host.Children.Add(BuildQuote(quote, palette));
                continue;
            }

            if (TryListMarker(line, out _))
            {
                while (index < lines.Length && TryListMarker(lines[index], out var item))
                {
                    host.Children.Add(BuildListItem(item, palette));
                    index++;
                }

                continue;
            }

            // 普通段落：连续的非块级行合并为一个文本块，保留原始换行。
            var paragraph = new List<string>();
            while (index < lines.Length)
            {
                var candidate = lines[index];
                if (candidate.Trim().Length == 0 ||
                    (paragraph.Count > 0 && IsBlockStart(candidate)))
                {
                    break;
                }

                paragraph.Add(candidate.Trim());
                index++;
            }

            if (paragraph.Count == 0)
            {
                // 防御：理论上不会命中，避免异常行导致死循环。
                index++;
                continue;
            }

            host.Children.Add(BuildParagraph(string.Join("\n", paragraph), palette));
        }

        if (truncated)
        {
            host.Children.Add(new TextBlock
            {
                Text = $"（正文过长，仅显示前 {MaxCharacters:N0} 个字符）",
                Margin = new Thickness(0, 10, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11.5 * palette.Scale,
                Foreground = palette.WeakBrush
            });
        }
    }

    /// <summary>把 Markdown 粗略压成纯文本，供悬停卡片摘要使用。</summary>
    public static string ToPlainText(string? markdown, int maxLength = 240)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return "";
        }

        var builder = new StringBuilder();
        var inFence = false;
        foreach (var raw in markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("```") || line.StartsWith("~~~"))
            {
                inFence = !inFence;
                continue;
            }

            if (line.Length == 0)
            {
                continue;
            }

            if (inFence)
            {
                builder.AppendLine(line);
                continue;
            }

            line = line.TrimStart('>', '#', ' ', '\t');
            if (line.StartsWith("- [x] ", StringComparison.OrdinalIgnoreCase))
            {
                line = "☑ " + line[6..];
            }
            else if (line.StartsWith("- [ ] ", StringComparison.Ordinal))
            {
                line = "☐ " + line[6..];
            }
            else
            {
                line = line.TrimStart('-', '*', '+', ' ');
            }

            line = line.Replace("**", "").Replace("__", "").Replace("~~", "").Replace("`", "").Replace("*", "");
            line = StripLinks(line);

            if (line.Length > 0)
            {
                builder.AppendLine(line);
            }

            if (builder.Length >= maxLength)
            {
                break;
            }
        }

        var plain = builder.ToString().Trim();
        return plain.Length <= maxLength ? plain : plain[..maxLength].TrimEnd() + "…";
    }

    private static string StripLinks(string line)
    {
        var result = new StringBuilder(line.Length);
        var i = 0;
        while (i < line.Length)
        {
            var start = line.IndexOf('[', i);
            if (start < 0)
            {
                result.Append(line, i, line.Length - i);
                break;
            }

            var labelStart = start + 1;
            if (start > 0 && line[start - 1] == '!')
            {
                labelStart = start + 1;
            }

            var labelEnd = line.IndexOf(']', labelStart);
            if (labelEnd < 0 || labelEnd + 1 >= line.Length || line[labelEnd + 1] != '(')
            {
                result.Append(line, i, start + 1 - i);
                i = start + 1;
                continue;
            }

            var urlEnd = line.IndexOf(')', labelEnd + 2);
            if (urlEnd < 0)
            {
                result.Append(line, i, line.Length - i);
                break;
            }

            // 图片语法 ![...](...) 一并去掉感叹号，只保留替代文字。
            var cut = start > 0 && line[start - 1] == '!' ? start - 1 : start;
            result.Append(line, i, Math.Max(0, cut - i));
            result.Append(line[labelStart..labelEnd]);
            i = urlEnd + 1;
        }

        return result.ToString();
    }

    private static bool IsBlockStart(string line)
    {
        var trimmed = line.Trim();
        return IsFence(trimmed, out _) ||
               TryHeading(trimmed, out _, out _) ||
               IsDivider(trimmed) ||
               trimmed.StartsWith('>') ||
               TryListMarker(line, out _);
    }

    private static bool IsFence(string trimmed, out string fence)
    {
        if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
        {
            fence = trimmed[..3];
            return true;
        }

        fence = "";
        return false;
    }

    private static bool IsClosingFence(string trimmed, string fence) =>
        trimmed.StartsWith(fence, StringComparison.Ordinal);

    private static bool TryHeading(string trimmed, out int level, out string text)
    {
        level = 0;
        while (level < trimmed.Length && level < 6 && trimmed[level] == '#')
        {
            level++;
        }

        if (level == 0 || level >= trimmed.Length || trimmed[level] != ' ')
        {
            level = 0;
            text = "";
            return false;
        }

        text = trimmed[(level + 1)..].Trim();
        return true;
    }

    private static bool IsDivider(string trimmed) =>
        trimmed is "---" or "***" or "___" ||
        (trimmed.Length >= 3 && trimmed.All(c => c == '-'));

    private sealed record ListItemLine(int Level, string Marker, string Content, bool IsTask, bool Done);

    private static bool TryListMarker(string line, out ListItemLine item)
    {
        item = new ListItemLine(0, "", "", false, false);

        var indent = 0;
        while (indent < line.Length && (line[indent] == ' ' || line[indent] == '\t'))
        {
            indent += line[indent] == '\t' ? 4 : 1;
        }

        var level = Math.Clamp(indent / 2, 0, 4);
        var body = line[indent..];

        if (body.Length >= 2 && (body[0] is '-' or '*' or '+') && body[1] == ' ')
        {
            var content = body[2..];
            var isTask = false;
            var done = false;
            if (content.Length >= 3 && content[0] == '[' && content[2] == ']' && content[1] is ' ' or 'x' or 'X')
            {
                isTask = true;
                done = content[1] is 'x' or 'X';
                content = content.Length > 3 ? content[3..].TrimStart() : "";
            }

            var marker = isTask ? "" : level switch
            {
                0 => "•",
                1 => "◦",
                2 => "▪",
                _ => "·"
            };
            item = new ListItemLine(level, marker, content, isTask, done);
            return true;
        }

        var digitEnd = 0;
        while (digitEnd < body.Length && char.IsDigit(body[digitEnd]))
        {
            digitEnd++;
        }

        if (digitEnd > 0 && digitEnd + 1 < body.Length &&
            body[digitEnd] == '.' && body[digitEnd + 1] == ' ')
        {
            item = new ListItemLine(
                level,
                body[..(digitEnd + 1)],
                body[(digitEnd + 2)..],
                false,
                false);
            return true;
        }

        return false;
    }

    private static TextBlock BuildParagraph(string text, PreviewPalette palette)
    {
        var block = CreateTextBlock(palette, 13.0, palette.TextBrush);
        block.LineHeight = 21.0 * palette.Scale;
        block.Margin = new Thickness(0, 2, 0, 7);
        AddInlines(block.Inlines, text, palette, 0);
        return block;
    }

    private static TextBlock BuildHeading(string text, int level, PreviewPalette palette)
    {
        var size = level switch
        {
            1 => 19.0,
            2 => 16.5,
            3 => 15.0,
            _ => 13.8
        };

        var block = CreateTextBlock(palette, size, palette.TextBrush);
        block.FontWeight = FontWeights.SemiBold;
        block.LineHeight = size * 1.45 * palette.Scale;
        block.Margin = new Thickness(0, 10, 0, 5);
        if (level == 1)
        {
            block.FontWeight = FontWeights.Bold;
        }

        AddInlines(block.Inlines, text, palette, 0);
        return block;
    }

    private static Border BuildDivider(PreviewPalette palette) => new()
    {
        Height = 1,
        Margin = new Thickness(0, 9, 0, 9),
        Background = palette.BorderBrush,
        Opacity = 0.6
    };

    private static Border BuildCodeBlock(string code, PreviewPalette palette)
    {
        var text = new TextBlock
        {
            Text = code,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = palette.MonoFontFamily,
            FontSize = 12.0 * palette.Scale,
            LineHeight = 18.5 * palette.Scale,
            Foreground = palette.TextBrush
        };

        return new Border
        {
            Margin = new Thickness(0, 4, 0, 8),
            Padding = new Thickness(10, 8, 10, 8),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            BorderBrush = palette.BorderBrush,
            Background = palette.SurfaceBrush,
            Child = text
        };
    }

    private static Border BuildQuote(IReadOnlyList<string> lines, PreviewPalette palette)
    {
        var content = new TextBlock
        {
            Text = string.Join("\n", lines).Trim(),
            TextWrapping = TextWrapping.Wrap,
            FontStyle = FontStyles.Italic,
            FontSize = 12.5 * palette.Scale,
            LineHeight = 20.0 * palette.Scale,
            Foreground = palette.WeakBrush
        };

        return new Border
        {
            Margin = new Thickness(0, 4, 0, 8),
            Padding = new Thickness(10, 6, 8, 6),
            BorderThickness = new Thickness(3, 0, 0, 0),
            BorderBrush = palette.AccentBrush,
            Background = palette.SurfaceBrush,
            Child = content
        };
    }

    private static Grid BuildListItem(ListItemLine item, PreviewPalette palette)
    {
        var row = new Grid
        {
            Margin = new Thickness(14 + (item.Level * 14), 2, 0, 2)
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var marker = new TextBlock
        {
            Text = item.IsTask ? (item.Done ? "" : "") : item.Marker,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Top,
            FontFamily = item.IsTask
                ? new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets")
                : palette.FontFamily,
            FontSize = (item.IsTask ? 13.0 : 13.0) * palette.Scale,
            LineHeight = 20.0 * palette.Scale,
            Foreground = item.IsTask && item.Done ? palette.AccentBrush : palette.WeakBrush
        };

        var text = CreateTextBlock(palette, 13.0, palette.TextBrush);
        text.LineHeight = 20.0 * palette.Scale;
        text.Opacity = item.Done ? 0.62 : 1.0;
        AddInlines(text.Inlines, item.Content, palette, 0);

        Grid.SetColumn(text, 1);
        row.Children.Add(marker);
        row.Children.Add(text);
        return row;
    }

    private static TextBlock CreateTextBlock(PreviewPalette palette, double size, Brush foreground) => new()
    {
        TextWrapping = TextWrapping.Wrap,
        FontFamily = palette.FontFamily,
        FontSize = size * palette.Scale,
        Foreground = foreground
    };

    private static void AddInlines(InlineCollection target, string text, PreviewPalette palette, int depth)
    {
        var plain = new StringBuilder();

        void Flush()
        {
            if (plain.Length == 0)
            {
                return;
            }

            target.Add(new Run(plain.ToString()));
            plain.Clear();
        }

        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];

            if (c == '\\' && i + 1 < text.Length)
            {
                plain.Append(text[i + 1]);
                i += 2;
                continue;
            }

            if (c == '\n')
            {
                Flush();
                target.Add(new LineBreak());
                i++;
                continue;
            }

            if (c == '`')
            {
                var end = text.IndexOf('`', i + 1);
                if (end > i + 1)
                {
                    Flush();
                    target.Add(new Run(text[(i + 1)..end])
                    {
                        FontFamily = palette.MonoFontFamily,
                        Background = palette.SurfaceBrush,
                        Foreground = palette.TextBrush
                    });
                    i = end + 1;
                    continue;
                }
            }

            if (c is '*' or '_' && depth < 3)
            {
                // 下划线只在词边界生效，避免把 snake_case 误判为斜体；
                // 星号不限制边界，否则“**重点**内容”这类中文写法无法识别。
                var underscore = c == '_';
                if (underscore && i > 0 && !IsBoundary(text[i - 1]))
                {
                    plain.Append(c);
                    i++;
                    continue;
                }

                var isTriple = c == '*' &&
                               i + 2 < text.Length &&
                               text[i + 1] == '*' &&
                               text[i + 2] == '*';
                var isDouble = !isTriple && c == '*' && i + 1 < text.Length && text[i + 1] == '*';
                var marker = isTriple ? "***" : isDouble ? "**" : c.ToString();
                var contentStart = i + marker.Length;
                var close = text.IndexOf(marker, contentStart, StringComparison.Ordinal);

                // 三个星号没有成对闭合时退化为两个星号，避免把 *** 当成普通粗体后残留星号。
                if (isTriple && close <= contentStart)
                {
                    isTriple = false;
                    isDouble = true;
                    marker = "**";
                    contentStart = i + 2;
                    close = text.IndexOf(marker, contentStart, StringComparison.Ordinal);
                }

                var closeAtEnd = close + marker.Length >= text.Length;
                if (close > contentStart &&
                    !char.IsWhiteSpace(text[contentStart]) &&
                    (!underscore || closeAtEnd || IsBoundary(text[close + marker.Length])))
                {
                    Flush();
                    if (isTriple)
                    {
                        var bold = new Bold();
                        var italic = new Italic();
                        AddInlines(italic.Inlines, text[contentStart..close], palette, depth + 1);
                        bold.Inlines.Add(italic);
                        target.Add(bold);
                    }
                    else
                    {
                        Span span = isDouble ? new Bold() : new Italic();
                        AddInlines(span.Inlines, text[contentStart..close], palette, depth + 1);
                        target.Add(span);
                    }

                    i = close + marker.Length;
                    continue;
                }
            }

            if (c == '[' || (c == '!' && i + 1 < text.Length && text[i + 1] == '['))
            {
                var image = c == '!';
                var labelStart = i + (image ? 2 : 1);
                var labelEnd = text.IndexOf(']', labelStart);
                if (labelEnd > labelStart && labelEnd + 1 < text.Length && text[labelEnd + 1] == '(')
                {
                    var urlEnd = text.IndexOf(')', labelEnd + 2);
                    if (urlEnd > labelEnd + 1)
                    {
                        Flush();
                        var label = text[labelStart..labelEnd];
                        target.Add(image
                            ? new Run("🖼 " + label) { Foreground = palette.WeakBrush }
                            : new Run(label)
                            {
                                Foreground = palette.AccentBrush,
                                TextDecorations = TextDecorations.Underline
                            });
                        i = urlEnd + 1;
                        continue;
                    }
                }
            }

            plain.Append(c);
            i++;
        }

        Flush();
    }

    /// <summary>行内强调只在词边界生效，避免把 snake_case 或 2*3 误判为格式。</summary>
    private static bool IsBoundary(char c) =>
        c == '\0' || char.IsWhiteSpace(c) || char.IsPunctuation(c);
}
