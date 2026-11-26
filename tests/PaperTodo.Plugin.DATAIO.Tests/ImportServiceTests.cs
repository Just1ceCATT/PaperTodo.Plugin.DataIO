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
