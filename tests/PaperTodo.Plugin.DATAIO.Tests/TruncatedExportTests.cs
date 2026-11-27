using System.Text.Json;
using Xunit;

namespace PaperTodo.Plugin.DataIo.Tests;

/// <summary>导出/导入被截断的备份只能新建，不能作为更新原纸片的权威数据。</summary>
public sealed class TruncatedExportTests
{
    [Fact]
    public void Todo_paper_over_limit_is_truncated_not_dropped()
    {
        var paper = new ExportPaper
        {
            SourceId = "s1",
            Type = "todo",
            Title = "大量待办",
            Todos = Enumerable.Range(0, ImportPlanParser.MaxTodosPerPaper + 50)
                .Select(index => new ExportTodo { Text = $"待办 {index}", Order = index })
                .ToList()
        };

        var plan = ImportPlanParser.Parse(Serialize(paper));

        Assert.Null(plan.FatalError);
        var imported = Assert.Single(plan.Papers);
        Assert.Equal(ImportPlanParser.MaxTodosPerPaper, imported.Source.Todos!.Count);
        Assert.True(imported.Source.TodosTruncated);
        Assert.Contains(plan.Warnings, warning => warning.Contains("不能用于更新"));
    }

    [Fact]
    public void Overlong_todo_text_is_truncated_and_marked()
    {
        var paper = new ExportPaper
        {
            SourceId = "s1",
            Type = "todo",
            Title = "超长待办",
            Todos =
            [
                new ExportTodo { Text = new string('x', ImportPlanParser.MaxTodoTextChars + 10), Order = 0 }
            ]
        };

        var plan = ImportPlanParser.Parse(Serialize(paper));

        var imported = Assert.Single(plan.Papers);
        Assert.Equal(ImportPlanParser.MaxTodoTextChars, imported.Source.Todos![0].Text.Length);
        Assert.True(imported.Source.TodosTruncated);
        Assert.Contains(plan.Warnings, warning => warning.Contains("不能用于更新"));
    }

    [Fact]
    public void Note_over_limit_is_truncated_and_marked()
    {
        var paper = new ExportPaper
        {
            SourceId = "s1",
            Type = "note",
            Title = "超长笔记",
            ContentAvailable = true,
            Content = new string('x', ImportPlanParser.MaxNoteChars + 10)
        };

        var plan = ImportPlanParser.Parse(Serialize(paper));

        var imported = Assert.Single(plan.Papers);
        Assert.Equal(ImportPlanParser.MaxNoteChars, imported.Source.Content!.Length);
        Assert.True(imported.Source.ContentTruncated);
        Assert.Contains(plan.Warnings, warning => warning.Contains("不能用于更新"));
    }

    [Fact]
    public void Note_without_content_is_marked_unimportable()
    {
        // 第三方插件绘制的正文无法读取：不能创建一张空笔记冒充恢复成功。
        var paper = new ExportPaper
        {
            SourceId = "s1",
            Type = "note",
            Title = "第三方笔记",
            ContentAvailable = false,
            Content = ""
        };

        var plan = ImportPlanParser.Parse(Serialize(paper));

        var imported = Assert.Single(plan.Papers);
        Assert.NotNull(imported.SkipReason);
        Assert.Contains(plan.Warnings, warning => warning.Contains("无法导入"));
    }

    [Fact]
    public void Link_to_paper_outside_file_is_flagged_for_preservation()
    {
        var paper = new ExportPaper
        {
            SourceId = "s1",
            Type = "todo",
            Title = "部分导出",
            Todos =
            [
                new ExportTodo
                {
                    Text = "关联外部纸片",
                    Order = 0,
                    LinkedSourcePaperId = "not-exported"
                }
            ]
        };

        var plan = ImportPlanParser.Parse(Serialize(paper));

        var imported = Assert.Single(plan.Papers);
        Assert.True(imported.Source.Todos![0].LinkedTargetOutsideFile);
        Assert.Equal(1, plan.UnresolvedLinks);
    }

    [Fact]
    public void Intact_paper_is_not_marked_truncated()
    {
        var paper = new ExportPaper
        {
            SourceId = "s1",
            Type = "todo",
            Title = "正常",
            Todos = [new ExportTodo { Text = "一条", Order = 0 }]
        };

        var plan = ImportPlanParser.Parse(Serialize(paper));

        var imported = Assert.Single(plan.Papers);
        Assert.False(imported.Source.TodosTruncated);
    }

    private static string Serialize(ExportPaper paper) =>
        JsonSerializer.Serialize(
            new ExportDocument
            {
                Format = ExportDocument.FormatName,
                SchemaVersion = ExportDocument.MinimumSupportedSchemaVersion,
                ExportedAt = DateTimeOffset.Now,
                Papers = [paper]
            },
            ExportJson.Options);
}
