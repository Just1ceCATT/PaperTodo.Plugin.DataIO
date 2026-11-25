using Xunit;

namespace PaperTodo.Plugin.DataIo.Tests;

/// <summary>
/// 更新式导入的图片安全边界：只有全部图片都能写回时才允许替换本地正文。
/// </summary>
public sealed class ImportImageSafetyTests
{
    [Fact]
    public void Restorable_json_image_does_not_block_update()
    {
        var paper = JsonPaper("![image|100%](i:001)");
        paper.Images.Add(Image("001", "images/p/001.png", restorable: true));

        Assert.Null(ImportService.GetImageRestoreBlocker(paper));
    }

    [Fact]
    public void Oversized_json_image_blocks_update()
    {
        var paper = JsonPaper("![image|100%](i:001)");
        paper.Images.Add(Image("001", "images/p/001.png", restorable: false));

        var blocker = ImportService.GetImageRestoreBlocker(paper);

        Assert.Contains("8 MiB", blocker);
    }

    [Fact]
    public void Missing_json_image_blocks_update()
    {
        var paper = JsonPaper("![image|100%](i:001)\n![image|100%](i:002)");
        paper.Images.Add(Image("001", "images/p/001.png", restorable: true));

        var blocker = ImportService.GetImageRestoreBlocker(paper);

        Assert.Contains("缺少", blocker);
    }

    [Fact]
    public void Plain_json_without_image_manifest_is_allowed()
    {
        var paper = JsonPaper("![image|100%](i:001)");

        Assert.Null(ImportService.GetImageRestoreBlocker(paper));
    }

    [Fact]
    public void Relative_link_without_asset_blocks_update()
    {
        var paper = JsonPaper("![image|100%](images/p/001.png)");
        paper.UsesRelativeImageLinks = true;

        var blocker = ImportService.GetImageRestoreBlocker(paper);

        Assert.Contains("缺少", blocker);
    }

    [Fact]
    public void Leftover_reserved_reference_in_markdown_package_blocks_update()
    {
        var paper = JsonPaper("![image|100%](i:001)");
        paper.UsesRelativeImageLinks = true;

        var blocker = ImportService.GetImageRestoreBlocker(paper);

        Assert.Contains("未包含", blocker);
    }

    [Fact]
    public void Image_url_normalization_ignores_local_and_package_address_forms()
    {
        const string local = "前言\n![image|100%](i:001)\n正文";
        const string source = "前言\n![image|100%](images/paper/001.png)\n正文";

        Assert.Equal(
            ImageReferenceScanner.NormalizeImageUrls(local),
            ImageReferenceScanner.NormalizeImageUrls(source));
    }

    [Fact]
    public void Image_url_normalization_keeps_external_urls_and_labels()
    {
        const string content = "![说明](https://example.com/a.png)\n![image|50%](i:002)";

        var normalized = ImageReferenceScanner.NormalizeImageUrls(content);

        Assert.Contains("https://example.com/a.png", normalized);
        Assert.Contains("![说明]", normalized);
        Assert.Contains("![image|50%](i:", normalized);
    }

    private static ImportPaper JsonPaper(string content) => new()
    {
        Source = new ExportPaper
        {
            Type = "note",
            ContentAvailable = true,
            Content = content
        }
    };

    private static ImportImageAsset Image(string sourceId, string file, bool restorable) => new()
    {
        SourceImageId = sourceId,
        File = file,
        Mime = "image/png",
        Bytes = [1, 2, 3],
        Restorable = restorable
    };
}
