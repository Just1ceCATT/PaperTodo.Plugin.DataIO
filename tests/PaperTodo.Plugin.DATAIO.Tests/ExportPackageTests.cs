using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Xunit;

namespace PaperTodo.Plugin.DataIo.Tests;

/// <summary>含图片导出的改写规则与压缩包布局。</summary>
public sealed class ExportPackageTests
{
    private static readonly byte[] ImageBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    [Fact]
    public void Rewrite_preserves_label_and_width()
    {
        const string content = "前文\n![说明|50%](i:123456)\n后文";

        var rewritten = ImageReferenceScanner.RewriteReferences(
            content,
            id => id == "123456" ? "images/abc/123456.png" : null);

        Assert.Equal("前文\n![说明|50%](images/abc/123456.png)\n后文", rewritten);
    }

    [Fact]
    public void Rewrite_keeps_invalid_ids_and_multiple_references()
    {
        const string content = "![x](i:123) ![y](i:123456789) ![z](i:654321)";

        var rewritten = ImageReferenceScanner.RewriteReferences(
            content,
            id => id == "123456789" ? "bad.png" : "images/abc/" + id + ".png");

        Assert.Equal(
            "![x](images/abc/123.png) ![y](i:123456789) ![z](images/abc/654321.png)",
            rewritten);
    }

    [Fact]
    public void Enumerate_returns_host_compatible_ids_in_order()
    {
        var references = ImageReferenceScanner.Enumerate(
            "![a](i:123) ![b](i:123456789) ![c](i:654321)");

        Assert.Equal(
            new[] { "123", "654321" },
            references.Select(reference => reference.ImageId));
    }

    [Fact]
    public void Json_zip_contains_document_and_images()
    {
        WithTempDirectory(directory =>
        {
            var path = Path.Combine(directory, "export.zip");
            var image = CreateImage("abc-123", "123456");
            var document = CreateDocument("![a](i:123456)", image);

            ExportPackageService.WriteJsonZip(path, document, [image]);

            using var archive = ZipFile.OpenRead(path);
            Assert.NotNull(archive.GetEntry(ExportPackageService.JsonEntryName));
            Assert.NotNull(archive.GetEntry(image.File));

            using var parsed = JsonDocument.Parse(ReadEntry(archive, ExportPackageService.JsonEntryName));
            Assert.Equal(2, parsed.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(
                image.File,
                parsed.RootElement.GetProperty("images")[0].GetProperty("file").GetString());
        });
    }

    [Fact]
    public void Markdown_zip_rewrites_references_and_writes_manifest()
    {
        WithTempDirectory(directory =>
        {
            var path = Path.Combine(directory, "export.zip");
            var image = CreateImage("abc-123", "123456");
            var document = CreateDocument("![说明|50%](i:123456)", image);

            ExportPackageService.WriteMarkdownZip(path, document, [image]);

            using var archive = ZipFile.OpenRead(path);
            var markdown = ReadEntry(archive, ExportPackageService.MarkdownEntryName);
            Assert.Contains($"![说明|50%]({image.File})", markdown);

            using var manifest = JsonDocument.Parse(
                ReadEntry(archive, ExportPackageService.MarkdownManifestEntryName));
            Assert.Equal(
                image.File,
                manifest.RootElement.GetProperty("images")[0].GetProperty("file").GetString());
            Assert.True(
                manifest.RootElement.GetProperty("images")[0].GetProperty("restorable").GetBoolean());
        });
    }

    [Fact]
    public void Image_entry_uses_safe_segments()
    {
        var entry = ExportPackageService.BuildImageEntry("纸片/一", "123456", "image/jpeg");

        Assert.StartsWith("images/", entry);
        Assert.DoesNotContain("纸", entry);
        Assert.EndsWith("/123456.jpg", entry);
    }

    [Fact]
    public void Schema_version_two_is_accepted_by_importer()
    {
        const string json =
            "{\"format\":\"papertodo.paper-export\",\"schemaVersion\":2,\"papers\":" +
            "[{\"type\":\"note\",\"title\":\"A\",\"content\":\"正文\"}]}";

        var plan = ImportPlanParser.Parse(json);

        Assert.Null(plan.FatalError);
        Assert.Single(plan.Papers);
    }

    private static ExportDocument CreateDocument(string content, CapturedExportImage image)
    {
        var document = new ExportDocument
        {
            SchemaVersion = ExportDocument.CurrentSchemaVersion,
            Papers =
            [
                new ExportPaper
                {
                    SourceId = image.PaperSourceId,
                    Type = "note",
                    Title = "笔记",
                    ContentAvailable = true,
                    Content = content
                }
            ]
        };
        document.Images = [ExportPackageService.ToManifest(image)];
        return document;
    }

    private static CapturedExportImage CreateImage(string paperSourceId, string imageId) => new(
        paperSourceId,
        imageId,
        ExportPackageService.BuildImageEntry(paperSourceId, imageId, "image/png"),
        "image/png",
        ImageBytes,
        Restorable: true);

    private static string ReadEntry(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name);
        Assert.NotNull(entry);
        using var stream = entry!.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
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
