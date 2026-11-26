using Xunit;

namespace PaperTodo.Plugin.DataIo.Tests;

/// <summary>导入页图片状态文案：正常匹配为灰色信息，异常情况才使用警告色。</summary>
public sealed class ImportImageStatusTests
{
    [Fact]
    public void No_image_information_returns_null()
    {
        Assert.Null(DataIoSession.BuildImageStatus(new ImportPlan()));
    }

    [Fact]
    public void All_images_matched_is_neutral_information()
    {
        var status = DataIoSession.BuildImageStatus(new ImportPlan
        {
            NotesWithImages = 1,
            ImageReferenceCount = 2,
            ImageAssetCount = 2,
            RestorableImageCount = 2
        });

        Assert.NotNull(status);
        Assert.False(status.Value.IsWarning);
        Assert.Equal("1 张纸片引用了 2 张图片，均可正常导入", status.Value.Text);
    }

    [Fact]
    public void Missing_references_use_warning_color()
    {
        var status = DataIoSession.BuildImageStatus(new ImportPlan
        {
            NotesWithImages = 1,
            ImageReferenceCount = 2,
            ImageAssetCount = 1,
            RestorableImageCount = 1,
            UnresolvedImageReferenceCount = 1
        });

        Assert.NotNull(status);
        Assert.True(status.Value.IsWarning);
        Assert.Equal("1 张纸片引用了 2 张图片，其中 1 张缺失", status.Value.Text);
    }

    [Fact]
    public void Unreferenced_assets_use_warning_color()
    {
        var status = DataIoSession.BuildImageStatus(new ImportPlan
        {
            ImageAssetCount = 2,
            UnreferencedImageAssetCount = 2
        });

        Assert.NotNull(status);
        Assert.True(status.Value.IsWarning);
        Assert.Equal("压缩包内有 2 张未被引用的图片", status.Value.Text);
    }

    [Fact]
    public void Missing_asset_files_use_warning_color()
    {
        var status = DataIoSession.BuildImageStatus(new ImportPlan
        {
            ImageAssetCount = 1,
            RestorableImageCount = 1,
            MissingImageAssetCount = 1
        });

        Assert.NotNull(status);
        Assert.True(status.Value.IsWarning);
        Assert.Equal("压缩包内有 1 张图片缺失或校验失败", status.Value.Text);
    }

    [Fact]
    public void Unrestorable_images_report_the_reason()
    {
        var compressionFailed = DataIoSession.BuildImageStatus(new ImportPlan
        {
            ImageAssetCount = 2,
            UnrestorableImageCount = 2,
            CompressionFailedImageCount = 2
        });

        Assert.NotNull(compressionFailed);
        Assert.True(compressionFailed.Value.IsWarning);
        Assert.Equal(
            "发现 2 张图片，但当前无法导入，格式不受支持或无法按宿主规则压缩到可写回的大小",
            compressionFailed.Value.Text);

        var oversized = DataIoSession.BuildImageStatus(new ImportPlan
        {
            ImageAssetCount = 1,
            UnrestorableImageCount = 1
        });

        Assert.NotNull(oversized);
        Assert.Equal(
            "发现 1 张图片，但当前无法导入，超过插件 8 MiB 写入上限",
            oversized.Value.Text);
    }

    [Fact]
    public void References_without_embedded_images_still_warn()
    {
        var status = DataIoSession.BuildImageStatus(new ImportPlan
        {
            NotesWithImages = 1,
            ImageReferenceCount = 2
        });

        Assert.NotNull(status);
        Assert.True(status.Value.IsWarning);
        Assert.Contains("文件未包含图片本体", status.Value.Text);
    }
}
