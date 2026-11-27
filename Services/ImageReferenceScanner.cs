using System.Text;

namespace PaperTodo.Plugin.DataIo;

/// <summary>
/// 识别内置 Markdown 笔记里的 PaperTodo 图片引用，例如 ![image|100%](i:123456)。
/// 导出时用于提取图片 ID、把引用改写成压缩包内的相对链接。
/// </summary>
/// <remarks>
/// 这里刻意不使用正则：旧的惰性模式 `!\[[^\r\n]*?\]\(` 在每个 "![" 起点都会向前扫描到行尾，
/// 遇到大量 "![" 却没有 "](" 的一行会退化成 O(n²)（10 万字符约 30 秒），
/// 构造的导入文件足以让界面长时间无响应。手工扫描按行定位第一个 "]("，
/// 并对同一 "](" 的解析失败结果做记忆化，整体保持线性复杂度。
/// </remarks>
internal static class ImageReferenceScanner
{
    // 与 PaperTodo 一致：图片 ID 为 3～8 位数字，标签内允许出现 ]。
    // PaperTodo 以第一个 "](" 作为标签结束位置（IndexOf("](", 2)），
    // 因此标签内容不排除 ]；否则 ![a]b](i:123456) 这类合法引用会被漏扫。
    // 统计、采集与改写必须共用同一套扫描规则，否则会出现“界面显示有图片但导出不采集”的丢图问题。

    private enum LinkKind
    {
        ImageId,
        RelativeLink
    }

    /// <summary>正文里一处图片引用；UrlStart/UrlLength 只覆盖地址本身，便于原地替换。</summary>
    private readonly record struct Link(
        LinkKind Kind,
        string ImageId,
        string Url,
        int Start,
        int UrlStart,
        int UrlLength,
        int End);

    public static int Count(string? markdown) =>
        CountLinks(markdown, includeImageIds: true, includeRelativeLinks: false);

    /// <summary>按出现顺序枚举正文里合法的图片引用；ID 规则与 PaperTodo 一致（3～8 位数字）。</summary>
    public static IReadOnlyList<ImageReferenceMatch> Enumerate(string? markdown)
    {
        var result = new List<ImageReferenceMatch>();
        ForEachLink(
            markdown,
            includeImageIds: true,
            includeRelativeLinks: false,
            link => result.Add(new ImageReferenceMatch(
                link.ImageId,
                link.Start,
                link.End - link.Start)));
        return result;
    }

    /// <summary>
    /// 把引用里的 i:ID 地址替换为解析器给出的新地址；解析器返回 null 时保留原引用。
    /// 标签、宽度等属性原样保留。
    /// </summary>
    public static string RewriteReferences(string? markdown, Func<string, string?> resolve)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            return markdown ?? "";
        }

        // 先枚举再替换：扫描依赖原始正文，不能在遍历过程中修改。
        var links = new List<Link>();
        ForEachLink(markdown, includeImageIds: true, includeRelativeLinks: false, links.Add);
        if (links.Count == 0)
        {
            return markdown;
        }

        return ReplaceUrls(markdown, links, link => resolve(link.ImageId));
    }

    /// <summary>
    /// 删除无法写回的图片引用，保留其余正文。返回新正文与被删除的引用数量。
    /// 用于“图片插入失败但文字仍要更新”的场景。
    /// </summary>
    public static (string Content, int Removed) RemoveUnresolvedReferences(
        string? markdown,
        Func<string, bool> isResolved)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            return (markdown ?? "", 0);
        }

        var links = new List<Link>();
        ForEachLink(markdown, includeImageIds: true, includeRelativeLinks: true, links.Add);
        if (links.Count == 0)
        {
            return (markdown, 0);
        }

        var builder = new StringBuilder(markdown.Length);
        var cursor = 0;
        var removed = 0;
        foreach (var link in links)
        {
            if (isResolved(link.Url))
            {
                continue;
            }

            builder.Append(markdown, cursor, link.Start - cursor);
            cursor = link.End;
            removed++;
        }

        if (removed == 0)
        {
            return (markdown, 0);
        }

        builder.Append(markdown, cursor, markdown.Length - cursor);
        return (builder.ToString(), removed);
    }

    /// <summary>
    /// 把本插件可控的图片地址（i:ID 或 images/ 相对链接）统一成 i:，
    /// 用于判断“本地 i:ID”与“文件中相对链接”是否只是写法不同。
    /// </summary>
    public static string NormalizeImageUrls(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            return markdown ?? "";
        }

        var links = new List<Link>();
        ForEachLink(markdown, includeImageIds: true, includeRelativeLinks: true, links.Add);
        return links.Count == 0 ? markdown : ReplaceUrls(markdown, links, _ => "i:");
    }

    /// <summary>统计 Markdown 压缩包相对链接的数量；这些链接在导入时会换成 i: 引用。</summary>
    public static int CountRelativeImageLinks(string? markdown) =>
        CountLinks(markdown, includeImageIds: false, includeRelativeLinks: true);

    /// <summary>按出现顺序枚举 Markdown 压缩包里的相对图片链接。</summary>
    public static IReadOnlyList<string> EnumerateRelativeImageLinks(string? markdown)
    {
        var result = new List<string>();
        ForEachLink(
            markdown,
            includeImageIds: false,
            includeRelativeLinks: true,
            link => result.Add(link.Url));
        return result;
    }

    /// <summary>
    /// 把相对图片链接替换为解析器给出的新地址（通常是 i:图片ID）；
    /// 解析器返回 null 时保留原链接。
    /// </summary>
    public static string RewriteRelativeImageLinks(
        string? markdown,
        Func<string, string?> resolve)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            return markdown ?? "";
        }

        var links = new List<Link>();
        ForEachLink(markdown, includeImageIds: false, includeRelativeLinks: true, links.Add);
        if (links.Count == 0)
        {
            return markdown;
        }

        return ReplaceUrls(markdown, links, link => resolve(link.Url));
    }

    /// <summary>
    /// 正文中是否含有“图片缺失”标记。旧版 Markdown 导出会把图片引用替换成该标记，
    /// 更新这类文件会破坏本地图片引用，导入时需要单独识别并拦截。
    /// </summary>
    public static bool ContainsMissingLabel(string? markdown, string label) =>
        !string.IsNullOrEmpty(markdown) &&
        markdown.Contains($"[{label}:", StringComparison.Ordinal);

    /// <summary>把图片引用替换为缺失标记（保留给旧版兼容与测试使用）。</summary>
    public static string ReplaceWithMissingLabel(string? markdown, string label)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            return markdown ?? "";
        }

        var links = new List<Link>();
        ForEachLink(markdown, includeImageIds: true, includeRelativeLinks: false, links.Add);
        if (links.Count == 0)
        {
            return markdown;
        }

        var builder = new StringBuilder(markdown.Length);
        var cursor = 0;
        foreach (var link in links)
        {
            builder.Append(markdown, cursor, link.Start - cursor);
            builder.Append('[').Append(label).Append(": ").Append(link.ImageId).Append(']');
            cursor = link.End;
        }

        builder.Append(markdown, cursor, markdown.Length - cursor);
        return builder.ToString();
    }

    /// <summary>按链接顺序替换地址部分；resolve 返回 null 时保留原地址。</summary>
    private static string ReplaceUrls(string markdown, List<Link> links, Func<Link, string?> resolve)
    {
        var builder = new StringBuilder(markdown.Length);
        var cursor = 0;
        foreach (var link in links)
        {
            var replacement = resolve(link);
            if (replacement is null)
            {
                continue;
            }

            builder.Append(markdown, cursor, link.UrlStart - cursor);
            builder.Append(replacement);
            cursor = link.UrlStart + link.UrlLength;
        }

        builder.Append(markdown, cursor, markdown.Length - cursor);
        return builder.ToString();
    }

    private static int CountLinks(string? markdown, bool includeImageIds, bool includeRelativeLinks)
    {
        var count = 0;
        ForEachLink(markdown, includeImageIds, includeRelativeLinks, _ => count++);
        return count;
    }

    /// <summary>
    /// 线性扫描正文里的图片引用。按行限制标签范围，并对同一 "](" 的解析失败做记忆化，
    /// 避免构造正文引发的二次回溯。
    /// </summary>
    private static void ForEachLink(
        string? markdown,
        bool includeImageIds,
        bool includeRelativeLinks,
        Action<Link> visit)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            return;
        }

        var index = 0;
        var lineEnd = -1;
        var failedClose = -1;
        while (index < markdown.Length)
        {
            var start = markdown.IndexOf("![", index, StringComparison.Ordinal);
            if (start < 0)
            {
                return;
            }

            if (lineEnd < start)
            {
                lineEnd = FindLineEnd(markdown, start);
            }

            var close = markdown.IndexOf("](", start + 2, StringComparison.Ordinal);
            if (close < 0 || close >= lineEnd)
            {
                // 本行剩余位置都不可能有可用的 "]("：直接跳到下一行。
                index = lineEnd < markdown.Length ? lineEnd + 1 : markdown.Length;
                continue;
            }

            if (close == failedClose)
            {
                // URL 解析只取决于 "](" 的位置，之前失败过就无需重复尝试。
                index = start + 2;
                continue;
            }

            if (!TryParseUrl(
                    markdown,
                    close + 2,
                    out var kind,
                    out var imageId,
                    out var urlStart,
                    out var urlLength,
                    out var end))
            {
                failedClose = close;
                index = start + 2;
                continue;
            }

            if (kind == LinkKind.ImageId ? includeImageIds : includeRelativeLinks)
            {
                visit(new Link(
                    kind,
                    imageId,
                    markdown.Substring(urlStart, urlLength),
                    start,
                    urlStart,
                    urlLength,
                    end));
            }

            index = end;
        }
    }

    /// <summary>返回从 position 起第一个换行符的位置；没有换行符时返回字符串长度。</summary>
    private static int FindLineEnd(string text, int position)
    {
        var index = position;
        while (index < text.Length && text[index] is not ('\r' or '\n'))
        {
            index++;
        }

        return index;
    }

    /// <summary>
    /// 从 "](" 之后解析可选空白、图片地址和右括号。
    /// 地址只接受 i:3～8 位数字或 images/ 下不含空白/右括号的相对链接。
    /// </summary>
    private static bool TryParseUrl(
        string text,
        int position,
        out LinkKind kind,
        out string imageId,
        out int urlStart,
        out int urlLength,
        out int end)
    {
        kind = default;
        imageId = "";
        urlStart = 0;
        urlLength = 0;
        end = 0;

        var cursor = position;
        while (cursor < text.Length && char.IsWhiteSpace(text[cursor]))
        {
            cursor++;
        }

        if (cursor >= text.Length)
        {
            return false;
        }

        urlStart = cursor;
        if (cursor + 1 < text.Length && text[cursor] == 'i' && text[cursor + 1] == ':')
        {
            var digitStart = cursor + 2;
            var digitEnd = digitStart;
            while (digitEnd < text.Length &&
                   digitEnd - digitStart < 8 &&
                   char.IsAsciiDigit(text[digitEnd]))
            {
                digitEnd++;
            }

            var digits = digitEnd - digitStart;
            if (digits < 3 ||
                // 第 9 位仍是数字：与旧的 \d{3,8} 一样按非法引用处理。
                (digitEnd < text.Length && char.IsAsciiDigit(text[digitEnd])))
            {
                return false;
            }

            kind = LinkKind.ImageId;
            imageId = text.Substring(digitStart, digits);
            urlLength = digitEnd - urlStart;
            cursor = digitEnd;
        }
        else if (cursor + 7 <= text.Length &&
                 text.AsSpan(cursor, 7).SequenceEqual("images/"))
        {
            var scan = cursor + 7;
            while (scan < text.Length && text[scan] != ')' && !char.IsWhiteSpace(text[scan]))
            {
                scan++;
            }

            if (scan == cursor + 7)
            {
                return false;
            }

            kind = LinkKind.RelativeLink;
            urlLength = scan - urlStart;
            cursor = scan;
        }
        else
        {
            return false;
        }

        while (cursor < text.Length && char.IsWhiteSpace(text[cursor]))
        {
            cursor++;
        }

        if (cursor >= text.Length || text[cursor] != ')')
        {
            return false;
        }

        end = cursor + 1;
        return true;
    }
}

/// <summary>正文里一处合法图片引用的位置。</summary>
internal readonly record struct ImageReferenceMatch(string ImageId, int Start, int Length);

/// <summary>一组笔记中的图片引用统计。</summary>
internal readonly record struct ImageReferenceSummary(int NoteCount, int ReferenceCount)
{
    public bool HasAny => ReferenceCount > 0;

    public static ImageReferenceSummary FromNotes(IEnumerable<string?> contents)
    {
        var notes = 0;
        var references = 0;
        foreach (var content in contents)
        {
            var count = ImageReferenceScanner.Count(content);
            if (count == 0)
            {
                continue;
            }

            notes++;
            references += count;
        }

        return new ImageReferenceSummary(notes, references);
    }
}
