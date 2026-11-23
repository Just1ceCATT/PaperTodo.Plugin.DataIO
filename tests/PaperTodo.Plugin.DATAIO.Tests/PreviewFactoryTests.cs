using Xunit;

namespace PaperTodo.Plugin.DataIo.Tests;

/// <summary>
/// 预览快照与列表告警的文案边界：空笔记不应被当成正文缺失，空待办要有自己的占位文案。
/// </summary>
public sealed class PreviewFactoryTests
{
    private const string Header =
        "{\"format\":\"papertodo.paper-export\",\"schemaVersion\":1,\"papers\":";

    [Fact]
    public void Empty_todo_preview_is_marked_as_todo()
    {
        var plan = ImportPlanParser.Parse(Header +
            "[{\"sourceId\":\"t\",\"type\":\"todo\",\"title\":\"空待办\",\"todos\":[]}]}");

        var content = PreviewFactory.BuildImport(plan, "0");

        Assert.NotNull(content);
        Assert.True(content!.IsTodo);
        Assert.Empty(content.Todos);
        Assert.Equal("暂无待办", content.Meta);
        Assert.True(content.ContentAvailable);
    }

    [Fact]
    public void Blank_note_is_available_and_has_no_warning()
    {
        var plan = ImportPlanParser.Parse(Header +
            "[{\"sourceId\":\"n\",\"type\":\"note\",\"title\":\"空笔记\"," +
            "\"contentAvailable\":true,\"content\":\"\"}]}");

        var content = PreviewFactory.BuildImport(plan, "0");

        Assert.NotNull(content);
        Assert.False(content!.IsTodo);
        Assert.True(content.ContentAvailable);
        // 预览可以提示“空白”，但不应声称文件缺少正文。
        Assert.DoesNotContain("未包含", content.Warning ?? "");
        Assert.Equal("空白笔记", content.Meta);
    }

    [Fact]
    public void Missing_note_body_keeps_warning()
    {
        var plan = ImportPlanParser.Parse(Header +
            "[{\"sourceId\":\"n\",\"type\":\"note\",\"title\":\"缺正文\"," +
            "\"contentAvailable\":false}]}");

        var content = PreviewFactory.BuildImport(plan, "0");

        Assert.NotNull(content);
        Assert.False(content!.ContentAvailable);
        Assert.Contains("未包含", content.Warning);
    }

    [Fact]
    public void Import_row_warning_does_not_flag_present_but_blank_note()
    {
        var plan = ImportPlanParser.Parse(Header +
            "[{\"sourceId\":\"n\",\"type\":\"note\",\"title\":\"空笔记\"," +
            "\"contentAvailable\":true,\"content\":\"\"}]}");

        var warnings = DataIoSession.BuildImportRowWarnings(plan.Papers[0], selectable: true);

        Assert.DoesNotContain(warnings, warning => warning.Contains("未包含"));
    }

    [Fact]
    public void Import_row_warning_reports_missing_note_body()
    {
        var plan = ImportPlanParser.Parse(Header +
            "[{\"sourceId\":\"n\",\"type\":\"note\",\"title\":\"缺正文\"," +
            "\"contentAvailable\":false}]}");

        var warnings = DataIoSession.BuildImportRowWarnings(plan.Papers[0], selectable: true);

        Assert.Contains(warnings, warning => warning.Contains("未包含"));
    }
}
