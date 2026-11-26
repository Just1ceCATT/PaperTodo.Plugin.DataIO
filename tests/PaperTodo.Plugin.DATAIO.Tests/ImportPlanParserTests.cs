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

    [Fact]
    public void Overlong_title_is_truncated_and_warned()
    {
        // 超长标题会被复制进提示、列表与持久化映射；解析时必须先收敛长度。
        var title = new string('长', 600);
        var json = Header +
            $"[{{\"sourceId\":\"a\",\"type\":\"note\",\"title\":\"{title}\",\"content\":\"正文\"}}]}}";

        var plan = ImportPlanParser.Parse(json);

        Assert.Null(plan.FatalError);
        var paper = Assert.Single(plan.Papers);
        Assert.Equal(500, paper.Source.Title.Length);
        Assert.Contains(plan.Warnings, warning => warning.Contains("标题超过"));
    }

    [Fact]
    public void Overlong_source_id_is_skipped_with_warning()
    {
        var sourceId = new string('a', 300);
        var json = Header +
            $"[{{\"sourceId\":\"{sourceId}\",\"type\":\"note\",\"title\":\"A\",\"content\":\"正文\"}}]}}";

        var plan = ImportPlanParser.Parse(json);

        Assert.Empty(plan.Papers);
        Assert.Contains(plan.Warnings, warning => warning.Contains("来源 ID"));
        // 唯一纸片被跳过后整份计划不可用，而不是带着超长 ID 继续导入。
        Assert.NotNull(plan.FatalError);
    }

    [Fact]
    public void File_source_ids_survive_partial_selection()
    {
        var json = Header +
            "[{\"sourceId\":\"a\",\"type\":\"todo\",\"title\":\"A\"," +
            "\"todos\":[{\"text\":\"甲\",\"linkedSourcePaperId\":\"b\"}]}," +
            "{\"sourceId\":\"b\",\"type\":\"note\",\"title\":\"B\",\"content\":\"正文\"}]}";

        var plan = ImportPlanParser.Parse(json);
        Assert.Null(plan.FatalError);
        Assert.Contains("b", plan.FileSourceIds);

        // 只勾选 A 时，B 虽未进入本轮计划，仍应记录在文件来源 ID 中，
        // 这样更新 A 时不会误清“A 的待办 → B”的本地关联。
        var filtered = plan.WithPapers([plan.Papers[0]]);

        Assert.Single(filtered.Papers);
        Assert.Contains("a", filtered.FileSourceIds);
        Assert.Contains("b", filtered.FileSourceIds);
    }
}
