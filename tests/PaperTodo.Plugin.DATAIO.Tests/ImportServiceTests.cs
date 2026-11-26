using Xunit;

namespace PaperTodo.Plugin.DataIo.Tests;

/// <summary>写入前的本地指纹复核。</summary>
public sealed class ImportServiceTests
{
    [Fact]
    public void Missing_expected_fingerprint_is_treated_as_match()
    {
        Assert.True(ImportService.FingerprintMatches(null, "anything"));
        Assert.True(ImportService.FingerprintMatches("", "anything"));
    }

    [Fact]
    public void Fingerprint_mismatch_blocks_write()
    {
        Assert.True(ImportService.FingerprintMatches("abc", "abc"));
        Assert.False(ImportService.FingerprintMatches("abc", "def"));
    }

    [Fact]
    public void Declared_unavailable_references_are_detected_for_updates()
    {
        var paper = new ImportPaper
        {
            Source = new ExportPaper
            {
                Type = "note",
                Content = "![a](i:123456) 和 ![b](i:654321)"
            }
        };

        Assert.False(ImportService.HasUnrestorableDeclaredReferences(paper));

        paper.UnavailableImageIds.Add("123456");
        Assert.True(ImportService.HasUnrestorableDeclaredReferences(paper));

        // 清单声明过、但正文没有引用的坏图片不需要触发“有变化”。
        paper.Source.Content = "没有图片引用";
        Assert.False(ImportService.HasUnrestorableDeclaredReferences(paper));

        // Markdown 包使用相对链接。
        paper.Source.Content = "![a](images/p/1.png)";
        paper.UsesRelativeImageLinks = true;
        paper.UnavailableImageFiles.Add("images/p/1.png");
        Assert.True(ImportService.HasUnrestorableDeclaredReferences(paper));
    }

    [Fact]
    public void Clearing_update_targets_resets_all_derived_state()
    {
        var paper = new ImportPaper
        {
            Source = new ExportPaper { Type = "note", Title = "示例" },
            UpdateTargetId = "p1",
            UpdateTargetTitle = "本地",
            UpdateTargetFingerprint = "fp",
            TargetLocallyModified = true,
            TargetLocalStateUnknown = true,
            TargetImagesAtRisk = true,
            UpdateDiff = [new DiffLine('-', "旧", 1, 0)],
            UpdateHasChanges = true,
            UpdateHasMetaChanges = true
        };
        var plan = new ImportPlan();
        plan.Papers.Add(paper);

        DataIoSession.ClearUpdateTargets(plan);

        Assert.Null(paper.UpdateTargetId);
        Assert.Null(paper.UpdateTargetTitle);
        Assert.Null(paper.UpdateTargetFingerprint);
        Assert.False(paper.TargetLocallyModified);
        Assert.False(paper.TargetLocalStateUnknown);
        Assert.False(paper.TargetImagesAtRisk);
        Assert.Null(paper.UpdateDiff);
        Assert.False(paper.UpdateHasChanges);
        Assert.False(paper.UpdateHasMetaChanges);
    }
}
