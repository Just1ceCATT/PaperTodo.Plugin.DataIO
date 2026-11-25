using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Xunit;

namespace PaperTodo.Plugin.DataIo.Tests;

/// <summary>含图片压缩包的导入解析与引用映射。</summary>
public sealed class ImportPackageTests
{
    private static readonly byte[] ImageBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    [Fact]
    public void Json_package_attaches_images_and_counts_references()
    {
        WithTempDirectory(directory =>
        {
            var image = CreateImage(restorable: true);
            var document = CreateDocument("![说明|50%](i:123456)");
            var path = WriteJsonPackage(directory, document, image);

            var plan = ImportPlanParser.ParseFile(path);

            Assert.Null(plan.FatalError);
            var paper = Assert.Single(plan.Papers);
            Assert.True(plan.HasEmbeddedImages);
            Assert.Equal(1, plan.ImageAssetCount);
            Assert.Equal(1, plan.RestorableImageCount);
            Assert.Equal(0, plan.UnrestorableImageCount);
            Assert.Equal(1, paper.ResolvedImageReferences);
            Assert.Equal(0, paper.UnresolvedImageReferences);
            Assert.False(paper.UsesRelativeImageLinks);
            var asset = Assert.Single(paper.Images);
            Assert.Equal("123456", asset.SourceImageId);
            Assert.Equal(ImageBytes, asset.Bytes);
            Assert.True(asset.Restorable);
        });
    }

    [Fact]
    public void Markdown_package_rewrites_links_and_attaches_images()
    {
        WithTempDirectory(directory =>
        {
            var image = CreateImage(restorable: true);
            var document = CreateDocument("![说明|50%](i:123456)");
            var path = Path.Combine(directory, "export.zip");
            ExportPackageService.WriteMarkdownZip(path, document, [image]);

            var plan = ImportPlanParser.ParseFile(path);

            Assert.Null(plan.FatalError);
            var paper = Assert.Single(plan.Papers);
            Assert.True(paper.UsesRelativeImageLinks);
            Assert.Equal(1, plan.ImageAssetCount);
            Assert.Equal(1, paper.ResolvedImageReferences);
            Assert.Equal(0, paper.UnresolvedImageReferences);
            Assert.Contains($"![说明|50%]({image.File})", paper.Source.Content);
        });
    }

    [Fact]
    public void Missing_image_file_is_reported_and_left_unresolved()
    {
        WithTempDirectory(directory =>
        {
            var image = CreateImage(restorable: true);
            var document = CreateDocument("![a](i:123456)");
            document.Images = [ExportPackageService.ToManifest(image)];
            var path = Path.Combine(directory, "export.zip");

            // 只写 JSON，不写图片条目，模拟文件缺失。
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                WriteEntry(
                    archive,
                    ExportPackageService.JsonEntryName,
                    JsonSerializer.Serialize(document, ExportJson.Options));
            }

            var plan = ImportPlanParser.ParseFile(path);

            Assert.Null(plan.FatalError);
            Assert.Equal(0, plan.ImageAssetCount);
            Assert.Equal(1, plan.MissingImageAssetCount);
            var paper = Assert.Single(plan.Papers);
            Assert.Empty(paper.Images);
            Assert.Equal(0, paper.ResolvedImageReferences);
            Assert.Equal(1, paper.UnresolvedImageReferences);
            Assert.Contains(plan.Warnings, warning => warning.Contains("缺失"));
        });
    }

    [Fact]
    public void Image_with_unsafe_path_is_rejected()
    {
        WithTempDirectory(directory =>
        {
            var image = CreateImage(restorable: true);
            var document = CreateDocument("![a](i:123456)");
            document.Images =
            [
                new ExportImage
                {
                    PaperSourceId = image.PaperSourceId,
                    ImageId = image.ImageId,
                    File = "../outside.png",
                    Mime = image.Mime,
                    ByteLength = image.Bytes.Length
                }
            ];
            var path = Path.Combine(directory, "export.zip");

            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                WriteEntry(
                    archive,
                    ExportPackageService.JsonEntryName,
                    JsonSerializer.Serialize(document, ExportJson.Options));
                WriteEntry(archive, "outside.png", ImageBytes);
            }

            var plan = ImportPlanParser.ParseFile(path);

            Assert.Null(plan.FatalError);
            Assert.Equal(0, plan.ImageAssetCount);
            Assert.Equal(1, plan.MissingImageAssetCount);
        });
    }

    [Fact]
    public void Manifest_marked_not_restorable_is_kept_but_not_restorable()
    {
        WithTempDirectory(directory =>
        {
            var image = CreateImage(restorable: false);
            var document = CreateDocument("![a](i:123456)");
            var path = WriteJsonPackage(directory, document, image);

            var plan = ImportPlanParser.ParseFile(path);

            Assert.Null(plan.FatalError);
            Assert.Equal(1, plan.ImageAssetCount);
            Assert.Equal(0, plan.RestorableImageCount);
            Assert.Equal(1, plan.UnrestorableImageCount);
            var paper = Assert.Single(plan.Papers);
            Assert.False(Assert.Single(paper.Images).Restorable);
            Assert.Contains(plan.Warnings, warning => warning.Contains("8 MiB"));
        });
    }

    [Fact]
    public void Sha_mismatch_skips_the_image()
    {
        WithTempDirectory(directory =>
        {
            var image = CreateImage(restorable: true);
            var document = CreateDocument("![a](i:123456)");
            var manifest = ExportPackageService.ToManifest(image);
            manifest.Sha256 = new string('0', 64);
            document.Images = [manifest];
            var path = Path.Combine(directory, "export.zip");

            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                WriteEntry(
                    archive,
                    ExportPackageService.JsonEntryName,
                    JsonSerializer.Serialize(document, ExportJson.Options));
                WriteEntry(archive, image.File, ImageBytes);
            }

            var plan = ImportPlanParser.ParseFile(path);

            Assert.Null(plan.FatalError);
            Assert.Equal(0, plan.ImageAssetCount);
            Assert.Equal(1, plan.MissingImageAssetCount);
        });
    }

    [Fact]
    public void Foreign_zip_is_rejected_with_clear_message()
    {
        WithTempDirectory(directory =>
        {
            var path = Path.Combine(directory, "foreign.zip");
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                WriteEntry(archive, "readme.txt", "hello");
            }

            var plan = ImportPlanParser.ParseFile(path);

            Assert.NotNull(plan.FatalError);
            Assert.Contains("DataIo", plan.FatalError);
        });
    }

    [Fact]
    public void Content_mapping_rewrites_ids_and_relative_links()
    {
        var jsonPaper = new ImportPaper
        {
            Source = new ExportPaper
            {
                Type = "note",
                Content = "![a|50%](i:123456) ![b](i:654321)"
            }
        };
        var mappedJson = ImportService.BuildContentWithNewImageIds(
            jsonPaper,
            new Dictionary<string, string> { ["123456"] = "999999" },
            new Dictionary<string, string>());

        Assert.Equal(
            "![a|50%](i:999999) ![b](i:654321)",
            mappedJson);

        var markdownPaper = new ImportPaper
        {
            Source = new ExportPaper
            {
                Type = "note",
                Content = "![a|50%](images/p1/123456.png)"
            },
            UsesRelativeImageLinks = true
        };
        var mappedMarkdown = ImportService.BuildContentWithNewImageIds(
            markdownPaper,
            new Dictionary<string, string>(),
            new Dictionary<string, string> { ["images/p1/123456.png"] = "777777" });

        Assert.Equal("![a|50%](i:777777)", mappedMarkdown);
    }

    private static CapturedExportImage CreateImage(bool restorable) => new(
        "p1",
        "123456",
        ExportPackageService.BuildImageEntry("p1", "123456", "image/png"),
        "image/png",
        ImageBytes,
        restorable);

    private static ExportDocument CreateDocument(string content) => new()
    {
        SchemaVersion = ExportDocument.CurrentSchemaVersion,
        Papers =
        [
            new ExportPaper
            {
                SourceId = "p1",
                Type = "note",
                Title = "笔记",
                ContentAvailable = true,
                Content = content
            }
        ]
    };

    private static string WriteJsonPackage(
        string directory,
        ExportDocument document,
        CapturedExportImage image)
    {
        document.Images = [ExportPackageService.ToManifest(image)];
        var path = Path.Combine(directory, "export.zip");
        ExportPackageService.WriteJsonZip(path, document, [image]);
        return path;
    }

    private static void WriteEntry(ZipArchive archive, string name, string text)
    {
        var entry = archive.CreateEntry(name);
        using var stream = entry.Open();
        stream.Write(Encoding.UTF8.GetBytes(text));
    }

    private static void WriteEntry(ZipArchive archive, string name, byte[] bytes)
    {
        var entry = archive.CreateEntry(name);
        using var stream = entry.Open();
        stream.Write(bytes);
    }

    private static void WithTempDirectory(Action<string> action)
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "papertodo-dataio-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            action(directory);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
