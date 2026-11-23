using Xunit;

namespace PaperTodo.Plugin.DataIo.Tests;

/// <summary>导入结果弹窗的计数口径：待办纸片按“张”，待办项按“条”。</summary>
public sealed class ImportResultStatsTests
{
    [Fact]
    public void Created_todos_show_paper_and_item_counts()
    {
        var result = new ImportResult
        {
            CreatedPapers = 3,
            CreatedTodoPapers = 2,
            CreatedTodos = 10
        };

        var stats = DataIoSession.BuildImportResultStats(result);

        Assert.Contains(("新增纸片", "3 张"), stats);
        Assert.Contains(("新增待办纸片", "2 张"), stats);
        Assert.Contains(("新增待办项", "10 条"), stats);
    }

    [Fact]
    public void Empty_todo_paper_shows_paper_count_only()
    {
        var result = new ImportResult
        {
            CreatedPapers = 1,
            CreatedTodoPapers = 1,
            CreatedTodos = 0
        };

        var stats = DataIoSession.BuildImportResultStats(result);

        Assert.Contains(("新增待办纸片", "1 张"), stats);
        Assert.DoesNotContain(stats, stat => stat.Label == "新增待办项");
    }

    [Fact]
    public void Updated_todos_show_paper_and_item_counts()
    {
        var result = new ImportResult
        {
            UpdatedPapers = 1,
            UpdatedTodoPapers = 1,
            UpdatedTodos = 6,
            DeletedTodos = 1
        };

        var stats = DataIoSession.BuildImportResultStats(result);

        Assert.Contains(("更新待办纸片", "1 张"), stats);
        Assert.Contains(("更新待办项", "6 条"), stats);
        Assert.Contains(("删除待办项", "1 条"), stats);
    }
}
