using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

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
    FontFamily MonoFontFamily,
    double ContentWidth);

/// <summary>
/// 轻量 Markdown 预览渲染：只覆盖标题、列表、引用、代码块、分隔线与常见行内样式，
/// 目标是把笔记内容以可读的排版展示出来，不追求完整的 Markdown 规范。
/// </summary>
internal static class MarkdownPreview
{
    /// <summary>单张纸片的渲染上限，避免超长正文让预览窗口长时间卡住。</summary>
    private const int MaxCharacters = 200_000;

    /// <summary>单张纸片最多生成的块数量；字符上限无法约束“每行一个列表项”的极端文件。</summary>
    private const int MaxBlocks = 2_000;

    /// <summary>单个段落最多生成的 Inline 数量，防止超长行被格式标记拆成海量 Run。</summary>
    private const int MaxInlinesPerBlock = 2_000;

    /// <summary>单张纸片最多解码的图片数量；超出后图片行退回为普通占位文本。</summary>
    private const int MaxImageBlocks = 60;

    /// <summary>解码宽度的上限（像素），避免大图在预览里占用过多内存。</summary>
    private const int MaxDecodePixelWidth = 2_000;

    /// <summary>解码位图的高度上限：细高图片按宽度解码会分配极其巨大的像素缓冲。</summary>
    private const int MaxDecodePixelHeight = 4_096;

    /// <summary>图片显示高度上限：防止 1×1000000 这类畸形图片把布局撑到不可用。</summary>
    private const double MaxDisplayImageHeight = 20_000;

    public static void Render(
        string markdown,
        Panel host,
        PreviewPalette palette,
        IReadOnlyDictionary<string, PreviewImage>? images = null)
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
        var imageBlocks = 0;
        var imageCache = new ImageRenderCache();

        while (index < lines.Length && host.Children.Count < MaxBlocks)
        {
            var line = lines[index];
            var trimmed = line.Trim();

            if (trimmed.Length == 0)
            {
                index++;
                continue;
            }

            if (TryParseImageLine(trimmed, out var imageLine))
            {
                if (imageBlocks < MaxImageBlocks)
                {
                    host.Children.Add(BuildImageBlock(imageLine, host, images, palette, imageCache));
                    imageBlocks++;
                }
                else
                {
                    host.Children.Add(BuildImageFallback(imageLine.Label, palette));
                }

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
                while (index < lines.Length &&
                       host.Children.Count < MaxBlocks &&
                       TryListMarker(lines[index], out var item))
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

        var hasRemainingContent = index < lines.Length &&
                                  lines.Skip(index).Any(line => line.Trim().Length > 0);
        if (hasRemainingContent)
        {
            // 命中块数上限：提前结束渲染并明确告知，避免界面看起来“已经完整显示”。
            host.Children.Add(new TextBlock
            {
                Text = $"（内容过多，仅显示前 {host.Children.Count:N0} 段）",
                Margin = new Thickness(0, 10, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11.5 * palette.Scale,
                Foreground = palette.WeakBrush
            });
        }
        else if (truncated)
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
            var labelEnd = line.IndexOf(']', labelStart);

            // 没有闭合的方括号：剩余文本不可能再构成链接，一次性追加，避免逐字符重复扫描。
            if (labelEnd < 0)
            {
                result.Append(line, i, line.Length - i);
                break;
            }

            if (labelEnd + 1 >= line.Length || line[labelEnd + 1] != '(')
            {
                // 无效链接：原样保留这段方括号内容并跳过，下一次从 ] 之后继续，
                // 防止大量 "[" 在无匹配时退化为 O(n²)。
                result.Append(line, i, labelEnd + 1 - i);
                i = labelEnd + 1;
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
               TryParseImageLine(trimmed, out _) ||
               TryListMarker(line, out _);
    }

    /// <summary>
    /// 解析独占一行的图片引用。宿主只把整行引用渲染为图片，
    /// 因此这里同样只接受“整行只有图片标记”（可带 {width=…} 属性）。
    /// </summary>
    private static bool TryParseImageLine(string trimmed, out MarkdownImageLine image)
    {
        image = default;
        if (!trimmed.StartsWith("![", StringComparison.Ordinal))
        {
            return false;
        }

        var labelEnd = trimmed.IndexOf(']', 2);
        if (labelEnd < 2 || labelEnd + 1 >= trimmed.Length || trimmed[labelEnd + 1] != '(')
        {
            return false;
        }

        var urlEnd = trimmed.IndexOf(')', labelEnd + 2);
        if (urlEnd < 0)
        {
            return false;
        }

        var url = trimmed[(labelEnd + 2)..urlEnd].Trim();
        if (!url.StartsWith("i:", StringComparison.Ordinal) &&
            !url.StartsWith("images/", StringComparison.Ordinal))
        {
            return false;
        }

        var tail = trimmed[(urlEnd + 1)..].Trim();
        if (tail.Length > 0 &&
            !(tail.StartsWith('{') && tail.EndsWith('}')))
        {
            return false;
        }

        image = new MarkdownImageLine(trimmed[2..labelEnd], url, tail);
        return true;
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

    /// <summary>整行图片引用：label 可能带 |100% / |300x200 等尺寸说明，attributes 为 {width=…}。</summary>
    private readonly record struct MarkdownImageLine(string Label, string Url, string Attributes);

    /// <summary>一次渲染内的图片解码缓存，避免同一张图片重复读取元数据或解码。</summary>
    private sealed class ImageRenderCache
    {
        public Dictionary<string, (int Width, int Height)> Natural { get; } =
            new(StringComparer.Ordinal);

        public Dictionary<string, BitmapSource?> Bitmaps { get; } =
            new(StringComparer.Ordinal);
    }

    private static FrameworkElement BuildImageBlock(
        MarkdownImageLine image,
        Panel host,
        IReadOnlyDictionary<string, PreviewImage>? images,
        PreviewPalette palette,
        ImageRenderCache cache)
    {
        if (images is null ||
            !images.TryGetValue(image.Url, out var data) ||
            data.Bytes.Length == 0)
        {
            return BuildImagePlaceholder("图片缺失或无法预览", image.Label, palette);
        }

        if (!cache.Natural.TryGetValue(image.Url, out var natural))
        {
            natural = ReadNaturalSize(data.Bytes);
            cache.Natural[image.Url] = natural;
        }

        // 元数据读取失败时退回完整解码，用解码后的尺寸继续排版。
        if (natural.Width <= 0 && !cache.Bitmaps.TryGetValue(image.Url, out var decoded))
        {
            decoded = DecodeBitmap(data.Bytes, 0);
            cache.Bitmaps[image.Url] = decoded;
            if (decoded is not null)
            {
                natural = (decoded.PixelWidth, decoded.PixelHeight);
                cache.Natural[image.Url] = natural;
            }
        }

        var available = Math.Max(80, palette.ContentWidth);
        var displayWidth = ResolveImageDisplayWidth(
            image.Label,
            image.Attributes,
            available,
            natural.Width);

        // 显示尺寸也按高度上限收敛：宽高比极端的图片不能把布局撑到数万像素高。
        if (natural is { Width: > 0, Height: > 0 })
        {
            var displayHeight = displayWidth * natural.Height / natural.Width;
            if (displayHeight > MaxDisplayImageHeight)
            {
                displayWidth = Math.Max(80, displayWidth * MaxDisplayImageHeight / displayHeight);
            }
        }

        if (!cache.Bitmaps.TryGetValue(image.Url, out var bitmap))
        {
            var pixelWidth = natural.Width > 0
                ? Math.Min(
                    natural.Width,
                    Math.Max(1, (int)Math.Ceiling(displayWidth * GetDpiScale(host))))
                : 0;
            bitmap = DecodeBitmap(
                data.Bytes,
                Math.Min(pixelWidth, MaxDecodePixelWidth),
                natural.Width,
                natural.Height);
            cache.Bitmaps[image.Url] = bitmap;
        }

        if (bitmap is null)
        {
            return BuildImagePlaceholder("图片已损坏，无法显示", image.Label, palette);
        }

        return new Border
        {
            Margin = new Thickness(0, 6, 0, 8),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new Image
            {
                Source = bitmap,
                Width = displayWidth,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Left,
                SnapsToDevicePixels = true,
                UseLayoutRounding = true,
                ToolTip = string.IsNullOrWhiteSpace(image.Label) ? null : image.Label
            }
        };
    }

    private static Border BuildImagePlaceholder(
        string message,
        string label,
        PreviewPalette palette) => new()
        {
            Margin = new Thickness(0, 6, 0, 8),
            Padding = new Thickness(12, 11, 12, 11),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            BorderBrush = palette.BorderBrush,
            Background = palette.SurfaceBrush,
            HorizontalAlignment = HorizontalAlignment.Left,
            MinWidth = 180,
            MaxWidth = Math.Max(180, palette.ContentWidth),
            Child = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11.5 * palette.Scale,
                Foreground = palette.WeakBrush,
                ToolTip = string.IsNullOrWhiteSpace(label) ? null : label
            },
            ToolTip = string.IsNullOrWhiteSpace(label) ? null : label
        };

    private static TextBlock BuildImageFallback(string label, PreviewPalette palette)
    {
        var block = CreateTextBlock(palette, 13.0, palette.WeakBrush);
        block.Margin = new Thickness(0, 3, 0, 3);
        block.Text = string.IsNullOrWhiteSpace(label) ? "🖼 图片" : "🖼 " + label;
        return block;
    }

    /// <summary>与宿主一致的宽度规则：属性优先，其次 |百分比、|宽x高，最后按原始尺寸且不超过容器。</summary>
    private static double ResolveImageDisplayWidth(
        string label,
        string attributeText,
        double contentWidth,
        int naturalWidth)
    {
        var available = Math.Max(80, contentWidth);
        naturalWidth = Math.Max(1, naturalWidth);

        double? width = null;
        if (TryParseAttributeWidth(attributeText, out var attributeValue, out var attributePercent))
        {
            width = attributePercent
                ? available * ClampPercent(attributeValue) / 100.0
                : attributeValue;
        }
        else
        {
            foreach (var spec in label.Split('|').Skip(1))
            {
                var trimmed = spec.Trim();
                if (TryParsePercent(trimmed, out var percent))
                {
                    width = available * ClampPercent(percent) / 100.0;
                    continue;
                }

                if (TryParseDimensionWidth(trimmed, out var pixels))
                {
                    width = pixels;
                }
            }
        }

        width ??= Math.Min(available, naturalWidth);
        return Math.Round(Math.Clamp(width.Value, 24, available), 1);
    }

    private static bool TryParseAttributeWidth(
        string attributeText,
        out double value,
        out bool isPercent)
    {
        value = 0;
        isPercent = false;
        var trimmed = attributeText.Trim();
        if (trimmed.Length < 3 ||
            !trimmed.StartsWith('{') ||
            !trimmed.EndsWith('}'))
        {
            return false;
        }

        var parts = trimmed[1..^1].Split(
            [' ', ',', ';'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            var separator = part.IndexOf('=');
            if (separator < 0)
            {
                separator = part.IndexOf(':');
            }

            if (separator <= 0 || separator >= part.Length - 1 ||
                !string.Equals(part[..separator].Trim(), "width", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var raw = part[(separator + 1)..].Trim();
            isPercent = raw.EndsWith('%');
            if (isPercent)
            {
                raw = raw[..^1];
            }
            else if (raw.EndsWith("px", StringComparison.OrdinalIgnoreCase))
            {
                raw = raw[..^2];
            }

            return double.TryParse(
                raw,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out value) && value > 0;
        }

        return false;
    }

    private static bool TryParsePercent(string value, out double percent)
    {
        percent = 0;
        var trimmed = value.Trim();
        return trimmed.EndsWith('%') &&
               double.TryParse(
                   trimmed[..^1],
                   System.Globalization.NumberStyles.Float,
                   System.Globalization.CultureInfo.InvariantCulture,
                   out percent) &&
               percent > 0;
    }

    private static bool TryParseDimensionWidth(string value, out double width)
    {
        width = 0;
        var normalized = value.Replace('×', 'x');
        var separator = normalized.IndexOf('x');
        var widthText = separator > 0 ? normalized[..separator] : normalized;
        return double.TryParse(
            widthText,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out width) && width > 0;
    }

    private static double ClampPercent(double percent) => Math.Clamp(percent, 1, 1_000);

    /// <summary>只读取图片头信息拿原始尺寸，不解码完整像素。</summary>
    private static (int Width, int Height) ReadNaturalSize(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.None);
            if (decoder.Frames.Count == 0)
            {
                return (0, 0);
            }

            var frame = decoder.Frames[0];
            return (frame.PixelWidth, frame.PixelHeight);
        }
        catch (Exception)
        {
            // 预览是辅助功能，任何解码/格式异常都退化为“无法显示”占位。
            return (0, 0);
        }
    }

    private static BitmapSource? DecodeBitmap(
        byte[] bytes,
        int pixelWidth,
        int naturalWidth = 0,
        int naturalHeight = 0)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bitmap.StreamSource = stream;

            if (pixelWidth > 0)
            {
                // WPF 只按宽度解码时高度按原比例放大；细高图片会分配超大位图。
                // 按高度上限反推一个更小的解码宽度，保持宽高比不变。
                if (naturalWidth > 0 && naturalHeight > 0)
                {
                    var expectedHeight = (long)pixelWidth * naturalHeight / naturalWidth;
                    if (expectedHeight > MaxDecodePixelHeight)
                    {
                        pixelWidth = Math.Max(
                            1,
                            (int)(pixelWidth * (double)MaxDecodePixelHeight / expectedHeight));
                    }
                }

                bitmap.DecodePixelWidth = pixelWidth;
            }

            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static double GetDpiScale(Visual visual)
    {
        try
        {
            var dpi = VisualTreeHelper.GetDpi(visual);
            return dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
        }
        catch (Exception)
        {
            return 1.0;
        }
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
            if (target.Count >= MaxInlinesPerBlock)
            {
                // 命中 Inline 上限时把剩余内容作为纯文本一次性追加，保留可读性。
                plain.Append(text[i..]);
                Flush();
                return;
            }

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

                // 没有闭合方括号：剩余内容不可能再构成链接，整段按普通文本追加，
                // 避免逐字符重复 IndexOf 在畸形输入上退化为 O(n²)。
                if (labelEnd < 0)
                {
                    plain.Append(text, i, text.Length - i);
                    i = text.Length;
                    continue;
                }

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

                // 无效链接：原样追加到 ] 为止并跳过，下一次从 ] 之后继续。
                plain.Append(text, i, labelEnd + 1 - i);
                i = labelEnd + 1;
                continue;
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
