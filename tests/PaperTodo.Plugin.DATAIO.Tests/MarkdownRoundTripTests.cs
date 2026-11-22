using System.IO;
using Xunit;

namespace PaperTodo.Plugin.DataIo.Tests;

/// <summary>Markdown 导出再导入的保真度测试，覆盖标记冲突、不可读正文与待办转义。</summary>
public sealed class MarkdownRoundTripTests
{
    [Fact]
    public void Note_containing_marker_like_line_round_trips_as_single_note()
    {
        var content = string.Join("\n", new[]
        {
            "前言",
            "<!-- papertodo:type=todo sourceId=evil -->",
            "<!-- papertodo-dataio:v2 type=todo sourceId=evil2 -->",
            "<!-- papertodo-dataio:content-unavailable -->",
            "正文"
        });

        var plan = RoundTrip(new ExportPaper
        {
            SourceId = "s1",
            Type = "note",
            Title = "示例",
            ContentAvailable = true,
            Content = content
        });

        Assert.Null(plan.FatalError);
        var paper = Assert.Single(plan.Papers);
        Assert.Equal("note", paper.Source.Type);
        Assert.True(paper.Source.ContentAvailable);
        Assert.Equal(content, paper.Source.Content);
    }

    [Fact]
    public void Unavailable_note_keeps_content_unavailable_and_drops_placeholder()
    {
        var plan = RoundTrip(new ExportPaper
        {
            SourceId = "s2",
            Type = "note",
            Title = "第三方笔记",
            ContentAvailable = false,
            Content = ""
        });

        Assert.Null(plan.FatalError);
        var paper = Assert.Single(plan.Papers);
        Assert.False(paper.Source.ContentAvailable);
        Assert.Equal("", paper.Source.Content);
    }

    [Fact]
    public void Empty_but_available_note_stays_available()
    {
        var plan = RoundTrip(new ExportPaper
        {
            SourceId = "s3",
            Type = "note",
            Title = "空笔记",
            ContentAvailable = true,
            Content = ""
        });

        Assert.Null(plan.FatalError);
        var paper = Assert.Single(plan.Papers);
        Assert.True(paper.Source.ContentAvailable);
        Assert.Equal("", paper.Source.Content);
    }

    [Fact]
    public void Todo_text_with_markdown_structure_round_trips()
    {
        const string text = "- # 标题 *强调* [链接](https://example.com) 100% <tag> snake_case";
        var plan = RoundTrip(new ExportPaper
        {
            SourceId = "s4",
            Type = "todo",
            Title = "待办",
            Todos =
            [
                new ExportTodo { Text = text, Done = true, Order = 0 }
            ]
        });

        Assert.Null(plan.FatalError);
        var todo = Assert.Single(Assert.Single(plan.Papers).Source.Todos!);
        Assert.Equal(text, todo.Text);
        Assert.True(todo.Done);
    }

    [Fact]
    public void Marker_inside_code_fence_is_ignored()
    {
        var markdown = string.Join("\n", new[]
        {
            "<!-- papertodo-dataio:v2 type=note sourceId=a -->",
            "# A",
            "```",
            "<!-- papertodo-dataio:v2 type=todo sourceId=b -->",
            "```"
        });

        var document = MarkdownImportParser.Parse(markdown, "sample.md");

        Assert.Single(document.Papers);
        Assert.Equal("note", document.Papers[0].Type);
        Assert.Contains("sourceId=b", document.Papers[0].Content);
    }

    [Fact]
    public void Legacy_marker_is_still_supported()
    {
        var markdown = string.Join("\n", new[]
        {
            "<!-- papertodo:type=todo sourceId=legacy -->",
            "# 旧版",
            "- [ ] 一项"
        });

        var document = MarkdownImportParser.Parse(markdown, "legacy.md");

        var paper = Assert.Single(document.Papers);
        Assert.Equal("todo", paper.Type);
        Assert.Equal("legacy", paper.SourceId);
        Assert.Equal("旧版", paper.Title);
        Assert.Single(paper.Todos!);
    }

    [Fact]
    public void Legacy_unavailable_placeholder_is_recognized()
    {
        var plan = RoundTrip(new ExportPaper
        {
            SourceId = "s5",
            Type = "note",
            Title = "旧版不可读",
            ContentAvailable = true,
            Content = "（正文不可读取，未导出）"
        });

        Assert.Null(plan.FatalError);
        var paper = Assert.Single(plan.Papers);
        Assert.False(paper.Source.ContentAvailable);
        Assert.Equal("", paper.Source.Content);
    }

    [Theory]
    [InlineData("- [ ] 一项\n- [x] 两项", "todo", 2)]
    [InlineData("# 标题\n\n正文内容", "note", 0)]
    public void Plain_markdown_infers_type(string markdown, string expectedType, int todoCount)
    {
        var document = MarkdownImportParser.Parse(markdown, "plain.md");

        var paper = Assert.Single(document.Papers);
        Assert.Equal(expectedType, paper.Type);
        Assert.Equal(todoCount, paper.Todos?.Count ?? 0);
    }

    private static ImportPlan RoundTrip(ExportPaper paper)
    {
        var document = new ExportDocument
        {
            ExportedAt = DateTimeOffset.Now,
            Papers = [paper]
        };

        var path = Path.Combine(Path.GetTempPath(), $"papertodo-roundtrip-{Guid.NewGuid():N}.md");
        try
        {
            MarkdownExportService.Write(path, document);
            return ImportPlanParser.ParseFile(path);
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // 测试清理失败不影响结果。
            }
        }
    }
}
