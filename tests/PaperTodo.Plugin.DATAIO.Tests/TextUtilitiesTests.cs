using Xunit;

namespace PaperTodo.Plugin.DataIo.Tests;

/// <summary>标题宽度、差异计算、正文规范化与指纹等纯文本逻辑。</summary>
public sealed class TextUtilitiesTests
{
    [Fact]
    public void Normalize_removes_control_characters_and_trims()
    {
        Assert.Equal("ab", TitleSanitizer.Normalize("  ab  "));
    }

    [Theory]
    [InlineData("中文", 4)]
    [InlineData("ab", 2)]
    [InlineData("A中", 3)]
    public void Display_width_counts_cjk_as_two(string text, int expected)
    {
        Assert.Equal(expected, TitleSanitizer.DisplayWidth(text));
    }

    [Fact]
    public void Truncate_never_splits_a_grapheme_cluster()
    {
        Assert.Equal("中", TitleSanitizer.TruncateByDisplayWidth("中文abc", 2));
        Assert.Equal("", TitleSanitizer.TruncateByDisplayWidth("中", 1));

        // 家庭 emoji 是多个码位组成的字素簇，按宽度 1 截断时不能留下半个代理项。
        var result = TitleSanitizer.TruncateByDisplayWidth("👨‍👩‍👧x", 1);
        Assert.Equal("", result);
    }

    [Fact]
    public void Title_limit_is_parsed_from_host_message()
    {
        Assert.True(TitleSanitizer.TryParseTitleLimit(
            "Title cannot exceed 12 full-width characters.", out var limit));
        Assert.Equal(12, limit);

        Assert.False(TitleSanitizer.TryParseTitleLimit("other error", out _));
    }

    [Fact]
    public void Diff_reports_removed_and_added_lines_with_context()
    {
        var diff = TextDiff.Compute("a\nb\nc", "a\nx\nc");

        Assert.Contains(diff, line => line.Kind == '-' && line.Text == "b" && line.OldLine == 2);
        Assert.Contains(diff, line => line.Kind == '+' && line.Text == "x" && line.NewLine == 2);
        Assert.Contains(diff, line => line.Kind == ' ' && line.Text == "a");
        Assert.Contains(diff, line => line.Kind == ' ' && line.Text == "c");
    }

    [Fact]
    public void Equal_content_produces_no_changes()
    {
        var diff = TextDiff.Compute("a\nb", "a\nb");

        Assert.All(diff, line => Assert.Equal(' ', line.Kind));
    }

    [Fact]
    public void Content_equivalence_ignores_trailing_blank_lines_and_separator()
    {
        Assert.True(MarkdownNormalize.ContentEquivalent("正文\n\n---\n\n", "正文"));
        Assert.False(MarkdownNormalize.ContentEquivalent("正文", "正文！"));
    }

    [Fact]
    public void Note_fingerprint_changes_with_content()
    {
        Assert.Equal(
            ImportIdentityService.ComputeNoteFingerprint("abc"),
            ImportIdentityService.ComputeNoteFingerprint("abc"));
        Assert.NotEqual(
            ImportIdentityService.ComputeNoteFingerprint("abc"),
            ImportIdentityService.ComputeNoteFingerprint("abd"));
    }

    [Fact]
    public void Todo_fingerprint_is_order_sensitive()
    {
        var first = CreateTodo("1", "甲", 0);
        var second = CreateTodo("2", "乙", 1);

        var forward = ImportIdentityService.ComputeTodoFingerprint([first, second]);
        var reversed = ImportIdentityService.ComputeTodoFingerprint(
            [CreateTodo("2", "乙", 0), CreateTodo("1", "甲", 1)]);

        Assert.Equal(forward, ImportIdentityService.ComputeTodoFingerprint([first, second]));
        Assert.NotEqual(forward, reversed);
    }

    [Fact]
    public void Image_reference_scanner_counts_host_style_references()
    {
        Assert.Equal(1, ImageReferenceScanner.Count("![图片](i:123456)"));
        Assert.Equal(2, ImageReferenceScanner.Count("![a](i:1) ![b](i:123456789012)"));
        Assert.Equal(0, ImageReferenceScanner.Count("![a](https://example.com/i:123)"));
    }

    private static PaperTodo.Plugin.TodoSnapshot CreateTodo(
        string id,
        string text,
        int order) => new(
        "paper",
        "标题",
        id,
        text,
        false,
        order,
        null,
        null,
        null);
}
