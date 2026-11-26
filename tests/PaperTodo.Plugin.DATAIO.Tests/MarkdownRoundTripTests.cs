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
    public void Unclosed_fence_does_not_swallow_following_papers()
    {
        var markdown = string.Join("\n", new[]
        {
            "<!-- papertodo-dataio:v2 type=note sourceId=a -->",
            "# A",
            "```",
            "code",
            "<!-- papertodo-dataio:v2 type=todo sourceId=b -->",
            "# B",
            "- [ ] 一项"
        });

        var document = MarkdownImportParser.Parse(markdown, "broken.md");

        Assert.Equal(2, document.Papers.Count);
        Assert.Equal("note", document.Papers[0].Type);
        Assert.Contains("```", document.Papers[0].Content);
        Assert.Equal("todo", document.Papers[1].Type);
        Assert.Equal("b", document.Papers[1].SourceId);
        Assert.Single(document.Papers[1].Todos!);
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
    public void Note_lines_equal_to_marker_comments_round_trip_without_backslash()
    {
        // 正文里恰好出现 title-empty / fence-closed 标记行：导出时转义，
        // 回导必须原样还原，不能残留反斜杠或丢失该行。
        var content =
            "<!-- papertodo-dataio:title-empty -->\n" +
            "<!-- papertodo-dataio:fence-closed -->\n" +
            "普通正文";
        var plan = RoundTrip(new ExportPaper
        {
            SourceId = "s6",
            Type = "note",
            Title = "标记行",
            ContentAvailable = true,
            Content = content
        });

        Assert.Null(plan.FatalError);
        var paper = Assert.Single(plan.Papers);
        Assert.True(paper.Source.ContentAvailable);
        Assert.Equal(content, paper.Source.Content);
    }

    [Fact]
    public void Note_line_with_user_escaped_marker_round_trips()
    {
        // 用户正文本来就写成 "\<!-- 标记 -->"：导出端补一层转义，回导剥一层后保持不变。
        var content = "\\<!-- papertodo-dataio:title-empty -->";
        var plan = RoundTrip(new ExportPaper
        {
            SourceId = "s7",
            Type = "note",
            Title = "用户转义",
            ContentAvailable = true,
            Content = content
        });

        Assert.Null(plan.FatalError);
        var paper = Assert.Single(plan.Papers);
        Assert.Equal(content, paper.Source.Content);
    }

    [Fact]
    public void Note_whose_text_equals_legacy_placeholder_is_preserved()
    {
        // v2 文件里正文恰好是这句占位文案时，它可能是真正的笔记内容，
        // 不能按旧版兼容规则清空。
        var content = "（正文不可读取，未导出）";
        var plan = RoundTrip(new ExportPaper
        {
            SourceId = "s5",
            Type = "note",
            Title = "真实内容",
            ContentAvailable = true,
            Content = content
        });

        Assert.Null(plan.FatalError);
        var paper = Assert.Single(plan.Papers);
        Assert.True(paper.Source.ContentAvailable);
        Assert.Equal(content, paper.Source.Content);
    }

    [Fact]
    public void Legacy_placeholder_file_without_v2_marker_is_still_recognized()
    {
        // 旧版导出没有 content-unavailable 标记，正文只有占位文案；
        // 识别只应作用于旧格式标记，不能波及 v2/普通 Markdown。
        var markdown =
            "<!-- papertodo:type=note sourceId=s5 -->\n" +
            "# 旧版不可读\n\n" +
            "（正文不可读取，未导出）\n";

        var document = MarkdownImportParser.Parse(markdown, "legacy.md");
        var paper = Assert.Single(document.Papers);

        Assert.False(paper.ContentAvailable);
        Assert.Equal("", paper.Content);
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

    [Fact]
    public void Unclosed_fence_does_not_swallow_next_paper_on_round_trip()
    {
        // A 的正文以未闭合代码围栏结束，B 又含代码块：旧实现会把 B 的标记当成
        // A 的代码内容，整张 B 被吞进 A。
        var document = new ExportDocument
        {
            ExportedAt = DateTimeOffset.Now,
            Papers =
            [
                new ExportPaper
                {
                    SourceId = "a",
                    Type = "note",
                    Title = "A",
                    ContentAvailable = true,
                    Content = "```\n未闭合的代码"
                },
                new ExportPaper
                {
                    SourceId = "b",
                    Type = "note",
                    Title = "B",
                    ContentAvailable = true,
                    Content = "```\n另一段代码\n```"
                }
            ]
        };

        var path = Path.Combine(Path.GetTempPath(), $"papertodo-fence-{Guid.NewGuid():N}.md");
        try
        {
            MarkdownExportService.Write(path, document);
            var plan = ImportPlanParser.ParseFile(path);

            Assert.Null(plan.FatalError);
            Assert.Equal(2, plan.Papers.Count);
            Assert.Equal("a", plan.Papers[0].Source.SourceId);
            Assert.Equal("```\n未闭合的代码", plan.Papers[0].Source.Content);
            Assert.Equal("b", plan.Papers[1].Source.SourceId);
            Assert.Equal("```\n另一段代码\n```", plan.Papers[1].Source.Content);
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

    [Fact]
    public void Empty_title_round_trips_as_empty()
    {
        var plan = RoundTrip(new ExportPaper
        {
            SourceId = "s6",
            Type = "note",
            Title = "",
            ContentAvailable = true,
            Content = "正文"
        });

        Assert.Null(plan.FatalError);
        Assert.Equal("", Assert.Single(plan.Papers).Source.Title);
    }

    [Fact]
    public void Blank_todo_text_is_written_to_markdown()
    {
        // 宿主不允许追加空文本待办，导入端会跳过它；但导出文件应保留该条目，
        // 让其它 Markdown 工具看到完整内容，而不是在导出时直接丢失。
        var document = new ExportDocument
        {
            ExportedAt = DateTimeOffset.Now,
            Papers =
            [
                new ExportPaper
                {
                    SourceId = "s7",
                    Type = "todo",
                    Title = "待办",
                    Todos =
                    [
                        new ExportTodo { Text = "", Done = false, Order = 0 },
                        new ExportTodo { Text = "有内容", Done = false, Order = 1 }
                    ]
                }
            ]
        };

        var markdown = MarkdownExportService.Build(document);
        var checkboxLines = markdown
            .Split('\n')
            .Count(line => line.StartsWith("- [ ]", StringComparison.Ordinal));

        Assert.Equal(2, checkboxLines);
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
