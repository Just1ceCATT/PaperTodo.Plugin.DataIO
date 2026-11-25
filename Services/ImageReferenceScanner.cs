using System.Text;
using System.Text.RegularExpressions;

namespace PaperTodo.Plugin.DataIo;

/// <summary>
/// 识别内置 Markdown 笔记里的 PaperTodo 图片引用，例如 ![image|100%](i:123456)。
/// 导出时用于提取图片 ID、把引用改写成压缩包内的相对链接。
/// </summary>
internal static partial class ImageReferenceScanner
{
    [GeneratedRegex(@"!\[[^\]\r\n]*\]\(\s*i:\d{1,12}\s*\)", RegexOptions.CultureInvariant)]
    private static partial Regex ReferencePattern();

    [GeneratedRegex(@"!\[[^\]\r\n]*\]\(\s*i:(\d{3,8})\s*\)", RegexOptions.CultureInvariant)]
    private static partial Regex ReferenceWithIdPattern();

    /// <summary>带 span 的引用匹配；url 组覆盖 i:ID 本身，方便只替换地址、保留标签与宽度。</summary>
    [GeneratedRegex(
        @"!\[[^\]\r\n]*\]\(\s*(?<url>i:(?<id>\d{3,8}))\s*\)",
        RegexOptions.CultureInvariant)]
    private static partial Regex RewritePattern();

    /// <summary>Markdown 压缩包使用的相对图片链接，例如 ![说明](images/abc/123456.png)。</summary>
    [GeneratedRegex(
        @"!\[[^\]\r\n]*\]\(\s*(?<url>images/[^)\s]+)\s*\)",
        RegexOptions.CultureInvariant)]
    private static partial Regex RelativeLinkPattern();

    /// <summary>只改写地址、保留标签的图片引用匹配；用于比较不同来源地址是否等价。</summary>
    [GeneratedRegex(
        @"(?<prefix>!\[[^\]\r\n]*\]\()\s*(?:i:\d{3,8}|images/[^)\s]+)\s*(?<suffix>\))",
        RegexOptions.CultureInvariant)]
    private static partial Regex ImageUrlPattern();

    public static int Count(string? markdown) =>
        string.IsNullOrEmpty(markdown) ? 0 : ReferencePattern().Matches(markdown).Count;

    /// <summary>按出现顺序枚举正文里合法的图片引用；ID 规则与宿主一致（3～8 位数字）。</summary>
    public static IReadOnlyList<ImageReferenceMatch> Enumerate(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            return [];
        }

        var matches = RewritePattern().Matches(markdown);
        if (matches.Count == 0)
        {
            return [];
        }

        var result = new List<ImageReferenceMatch>(matches.Count);
        foreach (Match match in matches)
        {
            result.Add(new ImageReferenceMatch(
                match.Groups["id"].Value,
                match.Index,
                match.Length));
        }

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

        var matches = RewritePattern().Matches(markdown);
        if (matches.Count == 0)
        {
            return markdown;
        }

        var builder = new StringBuilder(markdown.Length);
        var cursor = 0;
        foreach (Match match in matches)
        {
            var replacement = resolve(match.Groups["id"].Value);
            if (replacement is null)
            {
                continue;
            }

            var url = match.Groups["url"];
            builder.Append(markdown, cursor, url.Index - cursor);
            builder.Append(replacement);
            cursor = url.Index + url.Length;
        }

        builder.Append(markdown, cursor, markdown.Length - cursor);
        return builder.ToString();
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

        return ImageUrlPattern().Replace(
            markdown,
            match => match.Groups["prefix"].Value + "i:" + match.Groups["suffix"].Value);
    }

    /// <summary>统计 Markdown 压缩包相对链接的数量；这些链接在导入时会换成 i: 引用。</summary>
    public static int CountRelativeImageLinks(string? markdown) =>
        string.IsNullOrEmpty(markdown) ? 0 : RelativeLinkPattern().Matches(markdown).Count;

    /// <summary>按出现顺序枚举 Markdown 压缩包里的相对图片链接。</summary>
    public static IReadOnlyList<string> EnumerateRelativeImageLinks(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            return [];
        }

        var matches = RelativeLinkPattern().Matches(markdown);
        if (matches.Count == 0)
        {
            return [];
        }

        var result = new List<string>(matches.Count);
        foreach (Match match in matches)
        {
            result.Add(match.Groups["url"].Value);
        }

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

        var matches = RelativeLinkPattern().Matches(markdown);
        if (matches.Count == 0)
        {
            return markdown;
        }

        var builder = new StringBuilder(markdown.Length);
        var cursor = 0;
        foreach (Match match in matches)
        {
            var replacement = resolve(match.Groups["url"].Value);
            if (replacement is null)
            {
                continue;
            }

            var url = match.Groups["url"];
            builder.Append(markdown, cursor, url.Index - cursor);
            builder.Append(replacement);
            cursor = url.Index + url.Length;
        }

        builder.Append(markdown, cursor, markdown.Length - cursor);
        return builder.ToString();
    }

    /// <summary>
    /// 正文中是否含有“图片缺失”标记。旧版 Markdown 导出会把图片引用替换成该标记，
    /// 更新这类文件会破坏本地图片引用，导入时需要单独识别并拦截。
    /// </summary>
    public static bool ContainsMissingLabel(string? markdown, string label) =>
        !string.IsNullOrEmpty(markdown) &&
        markdown.Contains($"[{label}:", StringComparison.Ordinal);

    /// <summary>把图片引用替换为缺失标记（保留给旧版兼容与测试使用）。</summary>
    public static string ReplaceWithMissingLabel(string? markdown, string label) =>
        string.IsNullOrEmpty(markdown)
            ? markdown ?? ""
            : ReferenceWithIdPattern().Replace(
                markdown,
                match => $"[{label}: {match.Groups[1].Value}]");
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
