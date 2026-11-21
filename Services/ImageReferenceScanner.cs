using System.Text.RegularExpressions;

namespace PaperTodo.Plugin.DataIo;

/// <summary>
/// 识别内置 Markdown 笔记里的 PaperTodo 图片引用，例如 ![image|100%](i:123456)。
/// 归档只保存正文文本，不包含图片本体；这里用于在导出/导入时统计并提示风险。
/// </summary>
internal static partial class ImageReferenceScanner
{
    [GeneratedRegex(@"!\[[^\]\r\n]*\]\(\s*i:\d{3,8}\s*\)", RegexOptions.CultureInvariant)]
    private static partial Regex ReferencePattern();

    public static int Count(string? markdown) =>
        string.IsNullOrEmpty(markdown) ? 0 : ReferencePattern().Matches(markdown).Count;
}

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
