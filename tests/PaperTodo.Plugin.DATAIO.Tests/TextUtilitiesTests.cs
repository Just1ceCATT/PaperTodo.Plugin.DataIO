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
    public void Diff_shows_deletions_before_additions_for_a_changed_block()
    {
        var diff = TextDiff.Compute("a\nb\nc", "a\nx\ny\nc").ToList();

        var removed = diff.FindIndex(line => line.Kind == '-' && line.Text == "b");
        var addedX = diff.FindIndex(line => line.Kind == '+' && line.Text == "x");
        var addedY = diff.FindIndex(line => line.Kind == '+' && line.Text == "y");

        Assert.True(removed >= 0 && addedX > removed && addedY > addedX);
    }

    [Fact]
    public void Diff_pure_insertion_has_no_removed_lines()
    {
        var diff = TextDiff.Compute("a\nc", "a\nb\nc").ToList();

        Assert.DoesNotContain(diff, line => line.Kind == '-');
        var added = Assert.Single(diff, line => line.Kind == '+');
        Assert.Equal("b", added.Text);
        Assert.Equal(2, added.NewLine);
    }

    [Fact]
    public void Diff_pure_deletion_has_no_added_lines()
    {
        var diff = TextDiff.Compute("a\nb\nc", "a\nc").ToList();

        Assert.DoesNotContain(diff, line => line.Kind == '+');
        var removed = Assert.Single(diff, line => line.Kind == '-');
        Assert.Equal("b", removed.Text);
        Assert.Equal(2, removed.OldLine);
    }

    [Fact]
    public void Diff_groups_distant_changes_into_separate_hunks()
    {
        var oldLines = Enumerable.Range(1, 12).Select(i => $"line{i}").ToArray();
        var newLines = (string[])oldLines.Clone();
        newLines[1] = "changed2";
        newLines[10] = "changed11";

        var diff = TextDiff.Compute(string.Join("\n", oldLines), string.Join("\n", newLines)).ToList();

        var firstRemoved = diff.FindIndex(line => line.Kind == '-' && line.Text == "line2");
        var gap = diff.FindIndex(line => line.Kind == '…');
        var secondRemoved = diff.FindIndex(line => line.Kind == '-' && line.Text == "line11");

        // 两处修改之间应当出现省略行，而不是把中间所有内容都列出来。
        Assert.True(firstRemoved >= 0);
        Assert.True(gap > firstRemoved);
        Assert.True(secondRemoved > gap);
        Assert.DoesNotContain(diff, line => line.Kind == ' ' && line.Text == "line6");
    }

    [Fact]
    public void Diff_keeps_context_lines_around_each_change()
    {
        var oldLines = Enumerable.Range(1, 10).Select(i => $"line{i}").ToArray();
        var newLines = (string[])oldLines.Clone();
        newLines[4] = "changed5";

        var diff = TextDiff.Compute(string.Join("\n", oldLines), string.Join("\n", newLines)).ToList();

        Assert.Contains(diff, line => line.Kind == ' ' && line.Text == "line3");
        Assert.Contains(diff, line => line.Kind == ' ' && line.Text == "line4");
        Assert.Contains(diff, line => line.Kind == ' ' && line.Text == "line6");
        Assert.Contains(diff, line => line.Kind == ' ' && line.Text == "line7");
        Assert.DoesNotContain(diff, line => line.Kind == ' ' && line.Text == "line1");
        Assert.DoesNotContain(diff, line => line.Kind == ' ' && line.Text == "line10");
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
    public void Truncated_diff_shows_both_removals_and_additions()
    {
        // 1500 行整段改写会超过 LCS 上限并退化为“先全删、后全加”，
        // 截断必须同时保留两端的删除与新增，否则看起来像更新会清空正文。
        var oldText = string.Join("\n", Enumerable.Range(0, 1_500).Select(i => $"旧行 {i}"));
        var newText = string.Join("\n", Enumerable.Range(0, 1_500).Select(i => $"新行 {i}"));

        var diff = TextDiff.Compute(oldText, newText);

        Assert.Contains(diff, line => line.Kind == '-');
        Assert.Contains(diff, line => line.Kind == '+');
        Assert.Contains(diff, line => line.Kind == '…');
    }

    [Fact]
    public void Diff_ignores_terminal_newline_difference()
    {
        Assert.All(
            TextDiff.Compute("a\nb", "a\nb\n"),
            line => Assert.Equal(' ', line.Kind));
    }

    [Fact]
    public void Plain_text_summary_handles_pathological_brackets_quickly()
    {
        // 10 万个未闭合的 "["：旧实现逐个字符重复 IndexOf，会退化为 O(n²) 卡死 UI。
        var input = new string('[', 100_000);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var text = MarkdownPreview.ToPlainText(input);

        stopwatch.Stop();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"耗时 {stopwatch.Elapsed}");
        Assert.NotEqual("", text);

        // 有闭合方括号但没有链接目标，同样不能落入逐字符重复扫描。
        var closed = new string('[', 50_000) + new string(']', 50_000);
        stopwatch.Restart();
        _ = MarkdownPreview.ToPlainText(closed);
        stopwatch.Stop();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"耗时 {stopwatch.Elapsed}");
    }

    [Fact]
    public void Image_reference_scanner_counts_host_style_references()
    {
        Assert.Equal(1, ImageReferenceScanner.Count("![图片](i:123456)"));
        // 宿主只识别 3～8 位图片 ID；统计规则必须与采集/改写一致。
        Assert.Equal(0, ImageReferenceScanner.Count("![a](i:1) ![b](i:123456789012)"));
        Assert.Equal(1, ImageReferenceScanner.Count("![a](i:12345678)"));
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
