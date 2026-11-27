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
    public void Reference_missing_from_manifest_requires_removal_on_update()
    {
        // 压缩包清单只包含 001；正文还引用了导出时未读到的 002，
        // 本机同号图片可能是另一张图，更新必须移除该引用。
        var paper = JsonPaper("![a](i:001)\n![b](i:002)");
        paper.HasImageManifest = true;
        paper.Images.Add(Image("001", "images/p/001.png", restorable: true));

        Assert.True(ImportService.HasUnrestorableDeclaredReferences(paper));
    }

    [Fact]
    public void Fully_restorable_manifest_needs_no_removal()
    {
        var paper = JsonPaper("![a](i:001)");
        paper.HasImageManifest = true;
        paper.Images.Add(Image("001", "images/p/001.png", restorable: true));

        Assert.False(ImportService.HasUnrestorableDeclaredReferences(paper));
    }

    [Fact]
    public void Markdown_package_leftover_id_reference_requires_removal_on_update()
    {
        var paper = JsonPaper("![a](images/p/001.png)\n![b](i:002)");
        paper.UsesRelativeImageLinks = true;
        paper.Images.Add(Image("002", "images/p/001.png", restorable: true));

        Assert.True(ImportService.HasUnrestorableDeclaredReferences(paper));
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
    public void Removing_unresolved_references_keeps_text_and_resolved_images()
    {
        const string content =
            "第一段\n![image|100%](i:101)\n第二段\n![image|50%](i:102)\n第三段";

        var (result, removed) = ImageReferenceScanner.RemoveUnresolvedReferences(
            content,
            url => url == "i:101");

        Assert.Equal(1, removed);
        Assert.Contains("第一段", result);
        Assert.Contains("第二段", result);
        Assert.Contains("第三段", result);
        Assert.Contains("(i:101)", result);
        Assert.DoesNotContain("i:102", result);
    }

    [Fact]
    public void Removing_unresolved_relative_links_keeps_surrounding_text()
    {
        const string content =
            "开头\n![a](images/p1/101.png)\n中间\n![b](images/p1/102.png)\n结尾";

        var (result, removed) = ImageReferenceScanner.RemoveUnresolvedReferences(
            content,
            url => url.EndsWith("101.png", StringComparison.Ordinal));

        Assert.Equal(1, removed);
        Assert.Contains("images/p1/101.png", result);
        Assert.DoesNotContain("102.png", result);
        Assert.Contains("中间", result);
        Assert.Contains("结尾", result);
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
