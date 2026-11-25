using System.IO;
using Xunit;

namespace PaperTodo.Plugin.DataIo.Tests;

/// <summary>临时校验手动测试包能被解析，并正确映射两张穿插图片。</summary>
public sealed class ManualPackageValidationTests
{
    [Theory]
    [InlineData("manual-test-interleaved-json.zip", false)]
    [InlineData("manual-test-interleaved-markdown.zip", true)]
    public void Interleaved_package_parses_and_rewrites_images(
        string fileName,
        bool relativeLinks)
    {
        var path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "samples",
            fileName));

        var plan = ImportPlanParser.ParseFile(path);

        Assert.Null(plan.FatalError);
        Assert.Equal(2, plan.Papers.Count);
        var note = plan.Papers[0];
        Assert.Equal("note", note.Source.Type);
        Assert.Equal(2, note.Images.Count);
        Assert.Equal(relativeLinks, note.UsesRelativeImageLinks);
        Assert.Equal(2, plan.ImageAssetCount);
        Assert.Equal(2, plan.RestorableImageCount);
        Assert.Equal(0, plan.MissingImageAssetCount);

        if (relativeLinks)
        {
            Assert.Contains("images/manualtest-note-interleaved/101.png", note.Source.Content);
            Assert.Contains("images/manualtest-note-interleaved/102.png", note.Source.Content);
        }
        else
        {
            Assert.Contains("(i:101)", note.Source.Content);
            Assert.Contains("(i:102)", note.Source.Content);
        }

        var newIds = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["101"] = "501",
            ["102"] = "502"
        };
        var idsByFile = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["images/manualtest-note-interleaved/101.png"] = "501",
            ["images/manualtest-note-interleaved/102.png"] = "502"
        };

        var rewritten = ImportService.BuildContentWithNewImageIds(note, newIds, idsByFile);
        Assert.Contains("(i:501)", rewritten);
        Assert.Contains("(i:502)", rewritten);
        Assert.True(
            rewritten.IndexOf("(i:501)", StringComparison.Ordinal) <
            rewritten.IndexOf("(i:502)", StringComparison.Ordinal));
        Assert.Contains("第三段文字", rewritten);
    }
}
