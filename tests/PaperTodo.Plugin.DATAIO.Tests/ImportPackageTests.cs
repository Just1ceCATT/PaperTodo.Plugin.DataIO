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
            Assert.True(paper.HasImageManifest);
            Assert.Contains("123456", paper.UnavailableImageIds);
            Assert.Equal(0, paper.ResolvedImageReferences);
            Assert.Equal(1, paper.UnresolvedImageReferences);
            Assert.Contains(plan.Warnings, warning => warning.Contains("缺失"));

            // 清单声明过、但文件缺失的图片必须被视为“无法写回”，
            // 更新时才能按预告移除坏引用，而不是静默保留。
            Assert.Equal(
                "图片文件缺失或校验失败",
                ImportService.GetImageRestoreBlocker(paper));
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
            Assert.Contains(plan.Warnings, warning => warning.Contains("无法按宿主规则压缩"));
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

    [Fact]
    public void Core_backup_zip_merges_workspaces_into_one_plan()
    {
        WithTempDirectory(directory =>
        {
            var path = Path.Combine(directory, "PaperTodo-core-test.zip");
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                WriteEntry(archive, "data.json", CoreDocument("n1", "工作区1笔记"));
                WriteEntry(archive, "data2.json", CoreDocument("n2", "工作区2笔记"));
            }

            var plan = ImportPlanParser.ParseFile(path);

            Assert.Null(plan.FatalError);
            Assert.Equal(2, plan.Papers.Count);
            Assert.Equal("PaperTodo 备份压缩包", plan.ContainerFormat);
            Assert.Contains(plan.Warnings, warning => warning.Contains("已合并为 2 张纸片"));
            Assert.Contains(plan.Warnings, warning => warning.Contains("工作区边界不会保留"));
        });
    }

    [Fact]
    public void Core_backup_zip_rejects_duplicate_paper_ids_across_workspaces()
    {
        WithTempDirectory(directory =>
        {
            var path = Path.Combine(directory, "PaperTodo-core-test.zip");
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                WriteEntry(archive, "data.json", CoreDocument("same-id", "工作区1笔记"));
                WriteEntry(archive, "data2.json", CoreDocument("same-id", "工作区2笔记"));
            }

            var plan = ImportPlanParser.ParseFile(path);

            Assert.NotNull(plan.FatalError);
            Assert.Contains("相同的纸片 ID", plan.FatalError);
        });
    }

    [Fact]
    public void Core_backup_lists_workspaces_and_parses_only_selected()
    {
        WithTempDirectory(directory =>
        {
            var path = Path.Combine(directory, "PaperTodo-core-test.zip");
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                WriteEntry(archive, "data.json", CoreDocumentWithPluginNote());
                WriteEntry(archive, "data2.json", CoreDocument("n2", "工作区2笔记"));
            }

            Assert.True(
                ImportPlanParser.TryReadCoreBackupWorkspaces(path, out var workspaces, out var error));
            Assert.Null(error);
            Assert.Equal(2, workspaces.Count);
            Assert.Equal("data.json", workspaces[0].Name);
            Assert.Equal(1, workspaces[0].PaperCount);
            Assert.Equal(1, workspaces[0].SkippedPluginBodies);
            Assert.Equal("data2.json", workspaces[1].Name);
            Assert.Equal(1, workspaces[1].PaperCount);
            Assert.Equal(0, workspaces[1].SkippedPluginBodies);

            var selected = ImportPlanParser.ParseCoreBackup(path, ["data2.json"]);

            Assert.Null(selected.FatalError);
            Assert.Single(selected.Papers);
            Assert.Equal("n2", selected.Papers[0].Source.SourceId);
            Assert.Contains(
                selected.Warnings,
                warning => warning.Contains("检测到 PaperTodo 核心备份：data2.json"));
            Assert.DoesNotContain(
                selected.Warnings,
                warning => warning.Contains("工作区边界不会保留"));

            var empty = ImportPlanParser.ParseCoreBackup(path, []);
            Assert.NotNull(empty.FatalError);
            Assert.Contains("未选择任何工作区", empty.FatalError);
        });
    }

    [Fact]
    public void Core_backup_zip_skips_plugin_body_notes()
    {
        WithTempDirectory(directory =>
        {
            var path = Path.Combine(directory, "PaperTodo-core-test.zip");
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                WriteEntry(archive, "data.json", CoreDocumentWithPluginNote());
            }

            var plan = ImportPlanParser.ParseFile(path);

            Assert.Null(plan.FatalError);
            Assert.Single(plan.Papers);
            Assert.Contains(plan.Warnings, warning => warning.Contains("已跳过 1 张由其他插件绘制正文的纸片"));
        });
    }

    [Fact]
    public void Core_workspace_scan_ignores_dataio_export_packages()
    {
        WithTempDirectory(directory =>
        {
            var document = CreateDocument("导出笔记");
            var path = Path.Combine(directory, "export.zip");
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                WriteEntry(
                    archive,
                    ExportPackageService.JsonEntryName,
                    JsonSerializer.Serialize(document, ExportJson.Options));
                // 同时含根级 data.json：仍应按 DataIO 导出优先，而不是备份。
                WriteEntry(archive, "data.json", CoreDocument("n1", "工作区1笔记"));
            }

            Assert.False(
                ImportPlanParser.TryReadCoreBackupWorkspaces(path, out var workspaces, out _));
            Assert.Empty(workspaces);

            var plan = ImportPlanParser.ParseFile(path);
            Assert.Null(plan.FatalError);
            Assert.Equal("JSON 压缩包", plan.ContainerFormat);
            Assert.Single(plan.Papers);
        });
    }

    [Fact]
    public void Core_backup_reports_why_workspace_is_unreadable()
    {
        WithTempDirectory(directory =>
        {
            var path = Path.Combine(directory, "PaperTodo-core-bad.zip");
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                WriteEntry(archive, "data.json", "{ 不是有效 JSON");
            }

            var plan = ImportPlanParser.ParseFile(path);

            Assert.NotNull(plan.FatalError);
            Assert.Contains("没有可导入的纸片", plan.FatalError);
            Assert.Contains("data.json", plan.FatalError);
        });
    }

    [Fact]
    public void Unreferenced_image_asset_is_counted_and_reported()
    {
        WithTempDirectory(directory =>
        {
            var image = CreateImage(restorable: true);
            var document = CreateDocument("这张笔记没有引用任何图片。");
            var path = WriteJsonPackage(directory, document, image);

            var plan = ImportPlanParser.ParseFile(path);

            Assert.Null(plan.FatalError);
            Assert.Equal(1, plan.ImageAssetCount);
            Assert.Equal(0, plan.ImageReferenceCount);
            Assert.Equal(0, plan.NotesWithImages);
            Assert.Equal(1, plan.UnreferencedImageAssetCount);

            var status = DataIoSession.BuildImageStatus(plan);
            Assert.NotNull(status);
            Assert.True(status.Value.IsWarning);
            Assert.Equal("压缩包内有 1 张未被引用的图片", status.Value.Text);
        });
    }

    private static string CoreDocument(string id, string title) =>
        JsonSerializer.Serialize(new
        {
            papers = new[]
            {
                new
                {
                    id,
                    type = "note",
                    title,
                    bodyProviderId = "builtin.markdown",
                    content = "正文",
                    isVisible = true
                }
            }
        });

    private static string CoreDocumentWithPluginNote() =>
        JsonSerializer.Serialize(new
        {
            papers = new object[]
            {
                new
                {
                    id = "markdown-note",
                    type = "note",
                    title = "可读笔记",
                    bodyProviderId = "builtin.markdown",
                    content = "正文",
                    isVisible = true
                },
                new
                {
                    id = "plugin-note",
                    type = "note",
                    title = "插件笔记",
                    bodyProviderId = "sample.webhook.native",
                    content = "",
                    isVisible = true
                }
            }
        });

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
