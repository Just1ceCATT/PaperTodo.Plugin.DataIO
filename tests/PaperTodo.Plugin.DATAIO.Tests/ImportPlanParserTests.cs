using Xunit;

namespace PaperTodo.Plugin.DataIo.Tests;

/// <summary>导入文件校验：null 元素、重复 sourceId、类型与上限。</summary>
public sealed class ImportPlanParserTests
{
    private const string Header = "{\"format\":\"papertodo.paper-export\",\"schemaVersion\":1,\"papers\":";

    [Fact]
    public void Null_papers_and_null_todos_are_filtered_without_crashing()
    {
        var json = Header +
            "[null,{\"type\":\"note\",\"title\":\"A\",\"content\":\"正文\",\"todos\":[null]}]}";

        var plan = ImportPlanParser.Parse(json);

        Assert.Null(plan.FatalError);
        var paper = Assert.Single(plan.Papers);
        Assert.Equal("A", paper.Source.Title);
        Assert.Empty(paper.Source.Todos!);
    }

    [Fact]
    public void Duplicate_source_id_rejects_whole_file()
    {
        var json = Header +
            "[{\"sourceId\":\"a\",\"type\":\"todo\",\"title\":\"1\"}," +
            "{\"sourceId\":\"a\",\"type\":\"todo\",\"title\":\"2\"}]}";

        var plan = ImportPlanParser.Parse(json);

        Assert.NotNull(plan.FatalError);
        Assert.Contains("重复", plan.FatalError);
    }

    [Fact]
    public void Unknown_type_is_skipped_with_warning()
    {
        var json = Header +
            "[{\"sourceId\":\"a\",\"type\":\"webpage\",\"title\":\"网页\"}," +
            "{\"sourceId\":\"b\",\"type\":\"note\",\"title\":\"笔记\",\"content\":\"正文\"}]}";

        var plan = ImportPlanParser.Parse(json);

        Assert.Null(plan.FatalError);
        Assert.Single(plan.Papers);
        Assert.Contains(plan.Warnings, warning => warning.Contains("未知类型"));
    }

    [Fact]
    public void Note_with_todos_is_skipped()
    {
        var json = Header +
            "[{\"type\":\"note\",\"title\":\"混合\",\"content\":\"正文\"," +
            "\"todos\":[{\"text\":\"待办\"}]}]}";

        var plan = ImportPlanParser.Parse(json);

        Assert.NotNull(plan.FatalError);
        Assert.Contains("可导入", plan.FatalError);
    }

    [Fact]
    public void Invalid_json_returns_friendly_message()
    {
        var plan = ImportPlanParser.Parse("{");

        Assert.NotNull(plan.FatalError);
        Assert.Contains("JSON", plan.FatalError);
    }
}
