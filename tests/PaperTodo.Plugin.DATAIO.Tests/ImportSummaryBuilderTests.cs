using Xunit;

namespace PaperTodo.Plugin.DataIo.Tests;

/// <summary>导入确认文案：图片提示措辞与“预检提示”不再塞进确认正文。</summary>
public sealed class ImportSummaryBuilderTests
{
    [Fact]
    public void Image_hint_mentions_cross_device_risk()
    {
        var paper = new ImportPaper
        {
            Source = new ExportPaper
            {
                Type = "note",
                Title = "含图片",
                ContentAvailable = true,
                Content = "![图片](i:123)"
            }
        };

        var problems = ImportSummaryBuilder.BuildUnrecoverable([paper], new ImportPlan());

        Assert.Contains(problems, problem => problem.Contains("跨设备或原图已清理"));
    }

    [Fact]
    public void Confirmation_does_not_inline_precheck_warnings()
    {
        var plan = new ImportPlan();
        plan.Warnings.Add("示例预检提示");
        var paper = new ImportPaper
        {
            Source = new ExportPaper
            {
                Type = "todo",
                Title = "待办",
                Todos = []
            }
        };

        var confirmation = ImportSummaryBuilder.BuildConfirmation([paper], plan);

        Assert.DoesNotContain("另有", confirmation);
        Assert.DoesNotContain("示例预检提示", confirmation);
    }
}
