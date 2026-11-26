using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PaperTodo.Plugin;

namespace PaperTodo.Plugin.DataIo;

/// <summary>
/// 读取并校验导入文件。整个过程只处理内存数据，不接触宿主 API，可安全地在后台线程运行。
/// </summary>
internal static class ImportPlanParser
{
    public const int MaxPapers = 200;
    public const int MaxNoteChars = 100_000;
    public const int MaxTodoTextChars = 5_000;
    public const int MaxTodosPerPaper = 200;

    /// <summary>压缩包解压后允许的总字节数；与导出的图片总量上限保持一致。</summary>
    public const long MaxPackageExpandedBytes = ExportService.MaxEmbeddedImageBytes;

    private const int MaxPackageEntries = 5_000;
    private const int MaxPackageImages = 2_000;
    private const long MaxImageEntryBytes = IPaperNoteAssetsApi.MaximumImageBytes;

    public static ImportPlan ParseFile(string path)
    {
        var extension = Path.GetExtension(path);
        if (string.Equals(extension, ".zip", StringComparison.OrdinalIgnoreCase) ||
            LooksLikeZip(path))
        {
            return ParsePackage(path);
        }

        if (string.Equals(extension, ".md", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".markdown", StringComparison.OrdinalIgnoreCase))
        {
            return ParseMarkdown(JsonFiles.ReadAllText(path), path);
        }

        return Parse(DataFileService.ReadPayload(path));
    }

    private static bool LooksLikeZip(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Span<byte> signature = stackalloc byte[4];
            return stream.Read(signature) == signature.Length &&
                   signature[0] == (byte)'P' &&
                   signature[1] == (byte)'K' &&
                   signature[2] == 3 &&
                   signature[3] == 4;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// 读取含图片的导出压缩包。不落盘解压，只读取清单引用的条目；
    /// 文件缺失、校验失败或超限的图片会被跳过并记录提示。
    /// </summary>
    private static ImportPlan ParsePackage(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            return ParseArchive(archive, path);
        }
        catch (InvalidDataException)
        {
            return Fatal("压缩包已损坏或不是有效的 zip 文件。");
        }
        catch (IOException ex)
        {
            return Fatal($"读取压缩包失败：{ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return Fatal($"读取压缩包失败：{ex.Message}");
        }
    }

    private static ImportPlan ParseArchive(ZipArchive archive, string path)
    {
        if (archive.Entries.Count == 0)
        {
            return Fatal("压缩包为空。");
        }

        if (archive.Entries.Count > MaxPackageEntries)
        {
            return Fatal($"压缩包条目过多（{archive.Entries.Count} 个），已拒绝导入。");
        }

        long expandedBytes = 0;
        foreach (var entry in archive.Entries)
        {
            expandedBytes += entry.Length;
            if (expandedBytes > MaxPackageExpandedBytes)
            {
                return Fatal("压缩包解压后超过 512 MiB 上限，已拒绝导入。");
            }
        }

        var jsonEntry = UniqueEntry(archive, ExportPackageService.JsonEntryName);
        if (jsonEntry != null)
        {
            var json = ReadEntryText(jsonEntry, JsonFiles.MaxImportBytes);
            if (json is null)
            {
                return Fatal("压缩包中的导出 JSON 过大或无法读取。");
            }

            var plan = Parse(json);
            if (plan.FatalError != null)
            {
                return plan;
            }

            plan.ContainerFormat = "JSON 压缩包";
            AttachImages(plan, ReadJsonImageManifest(json), archive, usesRelativeLinks: false);
            return plan;
        }

        var markdownEntry = UniqueEntry(archive, ExportPackageService.MarkdownEntryName);
        if (markdownEntry != null)
        {
            var markdown = ReadEntryText(markdownEntry, JsonFiles.MaxImportBytes);
            if (markdown is null)
            {
                return Fatal("压缩包中的 Markdown 正文过大或无法读取。");
            }

            if (string.IsNullOrWhiteSpace(markdown))
            {
                return Fatal("压缩包中的 Markdown 正文为空。");
            }

            var plan = ParseDocument(MarkdownImportParser.Parse(markdown, path), supportsTodoMetadata: false);
            if (plan.FatalError == null)
            {
                plan.ContainerFormat = "Markdown 压缩包";
                plan.Warnings.Add("Markdown 文件不包含提醒、关联纸片、可见性等 PaperTodo 专有数据。");
            }

            var manifestEntry = UniqueEntry(archive, ExportPackageService.MarkdownManifestEntryName);
            var images = manifestEntry is null
                ? null
                : ReadMarkdownImageManifest(ReadEntryText(manifestEntry, 16L * 1024 * 1024));
            if (plan.FatalError == null)
            {
                AttachImages(plan, images, archive, usesRelativeLinks: true);
            }

            return plan;
        }

        // PaperTodo 核心历史备份：zip 内直接放 data.json / data2.json … data5.json。
        TryCollectCoreEntries(archive, out var coreEntries, out var duplicateCoreName);
        if (duplicateCoreName)
        {
            return Fatal("备份压缩包包含重复的工作区文件，已拒绝导入。");
        }

        if (coreEntries.Count > 0)
        {
            return ParseCoreBackup(coreEntries);
        }

        return Fatal(
            "压缩包不是由 DataIo 导出的，也不是 PaperTodo 备份：" +
            "缺少 dataio-export.json、dataio-export.md 或 data*.json。");
    }

    /// <summary>读取备份压缩包内的工作区列表；非核心备份返回 false。</summary>
    internal static bool TryReadCoreBackupWorkspaces(
        string path,
        out IReadOnlyList<CoreBackupWorkspace> workspaces,
        out string? error)
    {
        workspaces = [];
        error = null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            TryCollectCoreEntries(archive, out var entries, out var duplicate);
            if (duplicate || entries.Count == 0)
            {
                return false;
            }

            var list = new List<CoreBackupWorkspace>(entries.Count);
            foreach (var (number, name, entry) in entries)
            {
                var json = ReadEntryText(entry, JsonFiles.MaxImportBytes);
                if (json is null)
                {
                    list.Add(new CoreBackupWorkspace(number, name, 0, 0, 0, 0, false));
                    continue;
                }

                var built = BuildCoreExportDocument(json);
                if (built.Document is null)
                {
                    list.Add(new CoreBackupWorkspace(number, name, 0, 0, 0, 0, false));
                    continue;
                }

                var noteCount = built.Document.Papers.Count(
                    paper => string.Equals(paper.Type, "note", StringComparison.OrdinalIgnoreCase));
                var todoCount = built.Document.Papers.Count(
                    paper => string.Equals(paper.Type, "todo", StringComparison.OrdinalIgnoreCase));
                list.Add(new CoreBackupWorkspace(
                    number,
                    name,
                    built.Document.Papers.Count,
                    noteCount,
                    todoCount,
                    built.SkippedPluginBodies,
                    true));
            }

            workspaces = list;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            error = $"读取备份压缩包失败：{ex.Message}";
            return false;
        }
    }

    /// <summary>只解析用户在备份压缩包里勾选的工作区。</summary>
    internal static ImportPlan ParseCoreBackup(
        string path,
        IReadOnlyCollection<string> selectedNames)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            TryCollectCoreEntries(archive, out var entries, out var duplicate);
            if (duplicate || entries.Count == 0)
            {
                return Fatal("备份压缩包缺少有效的工作区文件。");
            }

            var selected = new HashSet<string>(selectedNames, StringComparer.Ordinal);
            var chosen = entries.Where(entry => selected.Contains(entry.Name)).ToList();
            if (chosen.Count == 0)
            {
                return Fatal("未选择任何工作区。");
            }

            return ParseCoreBackup(chosen);
        }
        catch (InvalidDataException)
        {
            return Fatal("压缩包已损坏或不是有效的 zip 文件。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fatal($"读取压缩包失败：{ex.Message}");
        }
    }

    /// <summary>收集并排序备份内的 dataN.json；返回 false 表示没有核心工作区文件。</summary>
    private static bool TryCollectCoreEntries(
        ZipArchive archive,
        out List<(int WorkspaceNumber, string Name, ZipArchiveEntry Entry)> entries,
        out bool duplicateName)
    {
        entries = [];
        duplicateName = false;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            if (!TryGetCoreWorkspaceNumber(entry.FullName, out var number))
            {
                continue;
            }

            if (!names.Add(entry.FullName))
            {
                duplicateName = true;
                return false;
            }

            entries.Add((number, entry.FullName, entry));
        }

        entries.Sort((left, right) => left.WorkspaceNumber.CompareTo(right.WorkspaceNumber));
        return entries.Count > 0;
    }

    /// <summary>
    /// 导入 PaperTodo 核心历史备份：每个 dataN.json 是一个工作区快照。
    /// 当前插件只能写入当前工作区，因此把全部文件合并为一个导入计划并提示边界不会保留。
    /// </summary>
    private static ImportPlan ParseCoreBackup(
        IReadOnlyList<(int WorkspaceNumber, string Name, ZipArchiveEntry Entry)> entries)
    {
        var combined = new ExportDocument();
        var workspaceDetails = new List<string>();
        var entryWarnings = new List<string>();
        var skippedPluginBodies = 0;
        var missingSourceIds = 0;

        foreach (var (_, name, entry) in entries)
        {
            var json = ReadEntryText(entry, JsonFiles.MaxImportBytes);
            if (json is null)
            {
                entryWarnings.Add($"{name} 无法读取或超过单次导入大小上限，已跳过。");
                continue;
            }

            var built = BuildCoreExportDocument(json);
            if (built.Document is null)
            {
                entryWarnings.Add($"{name} 不是有效的工作区数据，已跳过。");
                continue;
            }

            combined.Papers.AddRange(built.Document.Papers);
            skippedPluginBodies += built.SkippedPluginBodies;
            missingSourceIds += built.MissingSourceIds;
            workspaceDetails.Add($"{name}（{built.Document.Papers.Count} 张）");
        }

        if (combined.Papers.Count == 0)
        {
            return Fatal("备份压缩包中没有可导入的纸片。");
        }

        if (combined.Papers.Count > MaxPapers)
        {
            return Fatal(
                $"备份包含 {combined.Papers.Count} 张纸片，超过单次导入上限 {MaxPapers} 张；" +
                "请解压后逐个工作区导入。");
        }

        // 合并后 sourceId 必须唯一，否则同文件内的关联映射会产生歧义。
        var duplicateSourceId = combined.Papers
            .Where(paper => !string.IsNullOrWhiteSpace(paper.SourceId))
            .GroupBy(paper => paper.SourceId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateSourceId is not null)
        {
            return Fatal(
                $"备份中多个工作区包含相同的纸片 ID（{duplicateSourceId.Key}），无法一次性合并导入；" +
                "请解压后逐个工作区导入。");
        }

        var plan = ParseDocument(combined);
        if (plan.FatalError is not null)
        {
            return plan;
        }

        plan.ContainerFormat = "PaperTodo 备份压缩包";
        plan.Warnings.Insert(
            0,
            workspaceDetails.Count == 1
                ? $"检测到 PaperTodo 核心备份：{workspaceDetails[0]}。"
                : $"检测到 PaperTodo 核心备份：包含 {workspaceDetails.Count} 个工作区文件" +
                  $"（{string.Join("、", workspaceDetails)}），已合并为 {combined.Papers.Count} 张纸片；" +
                  "工作区边界不会保留。");
        plan.Warnings.Add("窗口位置、折叠/置顶状态、插件状态和图片本体不会保留。");
        foreach (var warning in entryWarnings)
        {
            plan.Warnings.Add(warning);
        }

        if (skippedPluginBodies > 0)
        {
            plan.Warnings.Add(
                $"已跳过 {skippedPluginBodies} 张由其他插件绘制正文的纸片，无法读取它们的正文。");
        }

        if (missingSourceIds > 0)
        {
            plan.Warnings.Add($"{missingSourceIds} 张纸片缺少来源 ID，再次导入时会新建副本。");
        }

        return plan;
    }

    /// <summary>data.json 为工作区 1，data2.json ～ data5.json 对应其余槽位。</summary>
    private static bool TryGetCoreWorkspaceNumber(string name, out int number)
    {
        number = 0;
        if (name == "data.json")
        {
            number = 1;
            return true;
        }

        if (name.Length == 10 &&
            name.StartsWith("data", StringComparison.Ordinal) &&
            name[4] is >= '2' and <= '5' &&
            name.EndsWith(".json", StringComparison.Ordinal))
        {
            number = name[4] - '0';
            return true;
        }

        return false;
    }

    private static ImportPlan Fatal(string message) => new() { FatalError = message };

    /// <summary>只接受唯一同名条目；重复条目视为不可信，返回 null。</summary>
    private static ZipArchiveEntry? UniqueEntry(ZipArchive archive, string name)
    {
        ZipArchiveEntry? found = null;
        foreach (var entry in archive.Entries)
        {
            if (!string.Equals(entry.FullName, name, StringComparison.Ordinal))
            {
                continue;
            }

            if (found != null)
            {
                return null;
            }

            found = entry;
        }

        return found;
    }

    private static string? ReadEntryText(ZipArchiveEntry entry, long maxBytes)
    {
        var bytes = ReadEntryBytes(entry, maxBytes);
        if (bytes is null)
        {
            return null;
        }

        var text = Encoding.UTF8.GetString(bytes);
        return text.Length > 0 && text[0] == '﻿' ? text[1..] : text;
    }

    /// <summary>
    /// 有上限地读取压缩包条目；不信任中央目录声明的长度，实际读取超过上限即失败。
    /// </summary>
    private static byte[]? ReadEntryBytes(ZipArchiveEntry entry, long maxBytes)
    {
        if (entry.Length > maxBytes)
        {
            return null;
        }

        try
        {
            using var stream = entry.Open();
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            long total = 0;
            int read;
            while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
            {
                total += read;
                if (total > maxBytes)
                {
                    return null;
                }

                buffer.Write(chunk, 0, read);
            }

            return buffer.ToArray();
        }
        catch (InvalidDataException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static List<ExportImage>? ReadJsonImageManifest(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<ExportDocument>(json, ExportJson.Options)?.Images;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static List<ExportImage>? ReadMarkdownImageManifest(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ExportImageManifest>(json, ExportJson.Options)?.Images;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>把清单中的图片附加到对应笔记，并统计可还原与缺失数量。</summary>
    private static void AttachImages(
        ImportPlan plan,
        List<ExportImage>? manifest,
        ZipArchive archive,
        bool usesRelativeLinks)
    {
        var notes = plan.Papers
            .Where(paper => string.Equals(paper.Source.Type, "note", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (manifest is { Count: > 0 })
        {
            if (manifest.Count > MaxPackageImages)
            {
                plan.Warnings.Add(
                    $"压缩包图片数量超过 {MaxPackageImages} 张上限，多余的图片不会写回笔记。");
                manifest = manifest.Take(MaxPackageImages).ToList();
            }

            var bySourceId = notes
                .Where(paper => !string.IsNullOrWhiteSpace(paper.Source.SourceId))
                .GroupBy(paper => paper.Source.SourceId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

            var entryCounts = archive.Entries
                .GroupBy(entry => entry.FullName, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

            var seenFiles = new HashSet<(string PaperSourceId, string File)>();
            var seenIds = new HashSet<(string PaperSourceId, string ImageId)>();
            var missing = 0;
            foreach (var image in manifest)
            {
                if (!IsSafeImageEntryPath(image.File) ||
                    !bySourceId.TryGetValue(image.PaperSourceId, out var paper))
                {
                    missing++;
                    continue;
                }

                if (!seenFiles.Add((image.PaperSourceId, image.File)) ||
                    !seenIds.Add((image.PaperSourceId, image.ImageId)))
                {
                    continue;
                }

                if (!entryCounts.TryGetValue(image.File, out var count) || count != 1)
                {
                    missing++;
                    continue;
                }

                var entry = archive.GetEntry(image.File)!;
                if (entry.Length <= 0 || entry.Length > MaxImageEntryBytes)
                {
                    missing++;
                    continue;
                }

                var bytes = ReadEntryBytes(entry, MaxImageEntryBytes);
                if (bytes is null || bytes.Length == 0 ||
                    (image.ByteLength > 0 && image.ByteLength != bytes.Length) ||
                    (!string.IsNullOrEmpty(image.Sha256) &&
                     !string.Equals(
                         Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                         image.Sha256,
                         StringComparison.OrdinalIgnoreCase)))
                {
                    missing++;
                    continue;
                }

                var storedBytes = bytes;
                var storedMime = image.Mime;
                var restorable = image.Restorable &&
                    bytes.LongLength <= IPaperNoteAssetsApi.MaximumImageAppendBytes;

                // 超过 8 MiB 时按宿主规则压缩后再写回；原始字节仍保留在压缩包内。
                if (bytes.LongLength > IPaperNoteAssetsApi.MaximumImageAppendBytes)
                {
                    if (ImageCompressionService.TryCompress(
                            bytes,
                            image.Mime,
                            out var compressed,
                            out _))
                    {
                        storedBytes = compressed.Bytes;
                        storedMime = compressed.Mime;
                        restorable = true;
                        plan.CompressedImageCount++;
                    }
                    else
                    {
                        restorable = false;
                        plan.CompressionFailedImageCount++;
                    }
                }

                paper.Images.Add(new ImportImageAsset
                {
                    SourceImageId = image.ImageId,
                    File = image.File,
                    Mime = storedMime,
                    Bytes = storedBytes,
                    Restorable = restorable,
                    Sha256 = image.Sha256
                });

                plan.ImageAssetCount++;
                if (restorable)
                {
                    plan.RestorableImageCount++;
                }
                else
                {
                    plan.UnrestorableImageCount++;
                }
            }

            plan.MissingImageAssetCount = missing;
        }
        else if (usesRelativeLinks)
        {
            plan.Warnings.Add("Markdown 压缩包缺少图片清单，正文中的相对图片链接不会写回为笔记图片。");
        }

        foreach (var paper in notes)
        {
            paper.UsesRelativeImageLinks = usesRelativeLinks;
            plan.UnreferencedImageAssetCount += CountImageReferences(paper, usesRelativeLinks);
            plan.UnresolvedImageReferenceCount += paper.UnresolvedImageReferences;
        }

        plan.NotesWithImages = notes.Count(
            paper => paper.ResolvedImageReferences + paper.UnresolvedImageReferences > 0);
        plan.ImageReferenceCount = notes.Sum(
            paper => paper.ResolvedImageReferences + paper.UnresolvedImageReferences);

        if (plan.CompressedImageCount > 0)
        {
            plan.Warnings.Add(
                $"{plan.CompressedImageCount} 张超过 8 MiB 的图片已按宿主规则压缩，" +
                "导入时写回压缩后的版本；原始图片仍保留在压缩包中。");
        }

        if (plan.UnrestorableImageCount > 0)
        {
            plan.Warnings.Add(
                $"{plan.UnrestorableImageCount} 张图片无法压缩到 8 MiB 以内" +
                "（例如 GIF/TIFF 或解码失败），只保留在压缩包中，导入时不会写回笔记。");
        }

        if (plan.MissingImageAssetCount > 0)
        {
            plan.Warnings.Add(
                $"{plan.MissingImageAssetCount} 张图片文件缺失或校验失败，相关引用将保持原样。");
        }

        if (plan.UnresolvedImageReferenceCount > 0)
        {
            plan.Warnings.Add(
                $"{plan.UnresolvedImageReferenceCount} 处图片引用在压缩包中没有对应图片，导入后可能无法显示。");
        }
    }

    /// <summary>统计正文引用，并返回压缩包里没有任何正文引用的图片数量。</summary>
    private static int CountImageReferences(ImportPaper paper, bool usesRelativeLinks)
    {
        var content = paper.Source.Content;
        if (usesRelativeLinks)
        {
            var files = paper.Images
                .Select(image => image.File)
                .ToHashSet(StringComparer.Ordinal);
            var referenced = new HashSet<string>(StringComparer.Ordinal);
            foreach (var link in ImageReferenceScanner.EnumerateRelativeImageLinks(content))
            {
                if (files.Contains(link))
                {
                    paper.ResolvedImageReferences++;
                    referenced.Add(link);
                }
                else
                {
                    paper.UnresolvedImageReferences++;
                }
            }

            // Markdown 包里未被改写的 i: 引用来自导出时读取失败的图片，无法在本机还原。
            paper.UnresolvedImageReferences += ImageReferenceScanner.Enumerate(content).Count;
            return paper.Images.Count(image => !referenced.Contains(image.File));
        }

        var imageIds = paper.Images
            .Select(image => image.SourceImageId)
            .ToHashSet(StringComparer.Ordinal);
        var referencedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reference in ImageReferenceScanner.Enumerate(content))
        {
            if (imageIds.Contains(reference.ImageId))
            {
                paper.ResolvedImageReferences++;
                referencedIds.Add(reference.ImageId);
            }
            else
            {
                paper.UnresolvedImageReferences++;
            }
        }

        return paper.Images.Count(image => !referencedIds.Contains(image.SourceImageId));
    }

    /// <summary>压缩包内的图片路径必须是 images/ 下的普通相对路径。</summary>
    private static bool IsSafeImageEntryPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            path.Contains('\\') ||
            path.Contains(':') ||
            path.StartsWith('/'))
        {
            return false;
        }

        if (!path.StartsWith("images/", StringComparison.Ordinal))
        {
            return false;
        }

        var segments = path.Split('/');
        return segments.All(segment =>
            segment.Length > 0 && segment is not "." and not "..");
    }

    private static ImportPlan ParseMarkdown(string markdown, string path)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return new ImportPlan { FatalError = "Markdown 文件为空。" };
        }

        // Markdown 解析结果直接走同一套校验，不再先序列化成 JSON 再解析。
        var plan = ParseDocument(MarkdownImportParser.Parse(markdown, path), supportsTodoMetadata: false);
        if (plan.FatalError == null)
        {
            plan.Warnings.Add("Markdown 文件不包含提醒、关联纸片、可见性等 PaperTodo 专有数据。");
        }

        return plan;
    }

    public static ImportPlan Parse(string json)
    {
        JsonDocument document;
        try
        {
            // 与宿主读取 data.json 的容错保持一致：允许注释和尾随逗号。
            document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
        }
        catch (JsonException)
        {
            return InvalidJsonPlan();
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Null)
            {
                return new ImportPlan { FatalError = "文件内容为空。" };
            }

            if (root.ValueKind != JsonValueKind.Object)
            {
                return UnsupportedPlan();
            }

            if (TryGetProperty(root, "format", out var format))
            {
                // 只有明确声明导出格式的文件才走导出解析；核心 data.json 没有 format 字段。
                if (format.ValueKind != JsonValueKind.String ||
                    !string.Equals(
                        format.GetString(),
                        ExportDocument.FormatName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return UnsupportedPlan();
                }

                return ParseExport(json, root);
            }

            // PaperTodo 主数据文件没有格式标记；还要确认纸片形状确实是核心数据，
            // 避免把“format 被删掉/改坏”的导出文件按错误字段静默读取。
            if (TryGetProperty(root, "papers", out var papers) &&
                papers.ValueKind == JsonValueKind.Array &&
                LooksLikeCoreData(root, papers))
            {
                return ParseCoreData(json);
            }

            return UnsupportedPlan();
        }
    }

    /// <summary>
    /// 解析插件自己的导出文件。schemaVersion 必须在原始 JSON 中确认：
    /// ExportDocument 的属性初始化器会让缺失字段也带上默认值，不能靠反序列化结果判断。
    /// </summary>
    private static ImportPlan ParseExport(string json, JsonElement root)
    {
        if (!TryGetProperty(root, "schemaVersion", out var schema) ||
            schema.ValueKind != JsonValueKind.Number ||
            !schema.TryGetInt32(out var version))
        {
            return new ImportPlan
            {
                FatalError = "文件缺少 schemaVersion，无法确认导出格式版本；请重新用 DataIO 导出。"
            };
        }

        ExportDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<ExportDocument>(json, ExportJson.Options);
        }
        catch (JsonException)
        {
            return InvalidJsonPlan();
        }

        if (document is null)
        {
            return new ImportPlan { FatalError = "文件内容为空。" };
        }

        if (version < ExportDocument.MinimumSupportedSchemaVersion ||
            version > ExportDocument.CurrentSchemaVersion)
        {
            return new ImportPlan
            {
                FatalError = version > ExportDocument.CurrentSchemaVersion
                    ? $"文件由更新版本的插件导出（schemaVersion={version}），请先升级插件。"
                    : $"不支持的文件版本（schemaVersion={version}）。"
            };
        }

        return ParseDocument(document);
    }

    /// <summary>
    /// 把 PaperTodo 主数据文件（data.json）转换为导出文档：
    /// id → sourceId（保证再次导入能命中映射），items → 待办，isVisible → 可见性。
    /// 第三方插件正文无法读取，直接跳过并提示，避免导入成空纸片。
    /// </summary>
    private static ImportPlan ParseCoreData(string json)
    {
        var built = BuildCoreExportDocument(json);
        if (built.Document is null)
        {
            return new ImportPlan { FatalError = built.Error };
        }

        var plan = ParseDocument(built.Document);
        if (plan.FatalError is not null)
        {
            if (built.SkippedPluginBodies > 0)
            {
                plan.FatalError +=
                    $"\n已跳过 {built.SkippedPluginBodies} 张由其他插件绘制正文的纸片，无法读取它们的正文。";
            }

            return plan;
        }

        plan.Warnings.Add(
            "检测到 PaperTodo 主数据文件（data.json）：将按纸片导入；" +
            "窗口位置、折叠/置顶状态和图片本体不会保留。");
        if (built.SkippedPluginBodies > 0)
        {
            plan.Warnings.Add(
                $"已跳过 {built.SkippedPluginBodies} 张由其他插件绘制正文的纸片，无法读取它们的正文。");
        }

        if (built.MissingSourceIds > 0)
        {
            plan.Warnings.Add(
                $"{built.MissingSourceIds} 张纸片缺少来源 ID，再次导入时会新建副本。");
        }

        return plan;
    }

    /// <summary>把单个工作区 dataN.json 转成导出文档；备份压缩包可复用并合并多个结果。</summary>
    private static CoreDocumentBuild BuildCoreExportDocument(string json)
    {
        CoreDataDocument? core;
        try
        {
            core = JsonSerializer.Deserialize<CoreDataDocument>(json, ExportJson.Options);
        }
        catch (JsonException)
        {
            return CoreDocumentBuild.Failed(InvalidJsonPlan().FatalError!);
        }

        if (core?.Papers is null)
        {
            return CoreDocumentBuild.Failed(UnsupportedPlan().FatalError!);
        }

        var document = new ExportDocument();
        var skippedPluginBodies = 0;
        var missingSourceIds = 0;

        foreach (var paper in core.Papers.OfType<CoreDataPaper>())
        {
            var type = (paper.Type ?? "").Trim().ToLowerInvariant();
            var markdownBody = IsMarkdownBody(paper.BodyProviderId);
            if (type == "note" && !markdownBody)
            {
                skippedPluginBodies++;
                continue;
            }

            var sourceId = string.IsNullOrWhiteSpace(paper.SourceId)
                ? (paper.Id ?? "").Trim()
                : paper.SourceId.Trim();
            if (sourceId.Length == 0)
            {
                missingSourceIds++;
            }

            var export = new ExportPaper
            {
                SourceId = sourceId,
                Type = paper.Type ?? "",
                Title = paper.Title ?? "",
                Show = paper.IsVisible
            };

            if (type == "note")
            {
                // 只有明确的字符串才算正文可读；缺失/null 时保留本地正文，禁止被空串覆盖。
                var hasContent = markdownBody && paper.Content.ValueKind == JsonValueKind.String;
                export.ContentAvailable = hasContent;
                export.Content = hasContent ? paper.Content.GetString() ?? "" : "";
            }
            else if (type == "todo")
            {
                export.Todos = (paper.Items ?? [])
                    .OfType<CoreDataItem>()
                    .Select(item => new ExportTodo
                    {
                        Text = item.Text ?? "",
                        Done = item.Done,
                        Order = item.Order,
                        ReminderAt = item.ReminderAt,
                        LinkedSourcePaperId = string.IsNullOrWhiteSpace(item.LinkedNoteId)
                            ? item.LinkedPaperId
                            : item.LinkedNoteId,
                        LinkedPath = item.LinkedPath,
                        LinkedPathIsDirectory = item.LinkedPathIsDirectory
                    })
                    .ToList();
            }

            document.Papers.Add(export);
        }

        return new CoreDocumentBuild(document, null, skippedPluginBodies, missingSourceIds);
    }

    private sealed record CoreDocumentBuild(
        ExportDocument? Document,
        string? Error,
        int SkippedPluginBodies,
        int MissingSourceIds)
    {
        public static CoreDocumentBuild Failed(string error) => new(null, error, 0, 0);
    }

    private static bool IsMarkdownBody(string? providerId) =>
        string.IsNullOrWhiteSpace(providerId) ||
        string.Equals(providerId.Trim(), PaperProviders.Markdown, StringComparison.Ordinal);

    /// <summary>
    /// 核心 data.json 的纸片使用 isVisible/items，而插件导出使用 sourceId/todos/show。
    /// 只有形状确实是核心数据时才走核心解析，避免误读被删掉 format 的导出文件。
    /// </summary>
    private static bool LooksLikeCoreData(JsonElement root, JsonElement papers)
    {
        foreach (var paper in papers.EnumerateArray())
        {
            if (paper.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (TryGetProperty(paper, "sourceId", out _) ||
                TryGetProperty(paper, "todos", out _) ||
                TryGetProperty(paper, "show", out _))
            {
                return false;
            }

            // 核心纸片使用 id/bodyProviderId/isVisible/items；导出纸片使用 sourceId/todos/show。
            if (TryGetProperty(paper, "id", out _) ||
                TryGetProperty(paper, "bodyProviderId", out _) ||
                TryGetProperty(paper, "isVisible", out _) ||
                TryGetProperty(paper, "items", out _))
            {
                return true;
            }
        }

        // 空数组或字段不完整时，用核心数据典型的顶层字段兜底。
        return TryGetProperty(root, "uiLanguage", out _) ||
               TryGetProperty(root, "theme", out _) ||
               TryGetProperty(root, "colorScheme", out _) ||
               TryGetProperty(root, "markdownRenderMode", out _) ||
               TryGetProperty(root, "todoVisualSize", out _);
    }

    /// <summary>属性名不区分大小写，兼容被其他工具改写为 PascalCase 的文件。</summary>
    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static ImportPlan InvalidJsonPlan() => new()
    {
        // 不向用户暴露底层解析器的英文错误；直接说明可能原因与处理方式。
        FatalError =
            "无法读取该文件：内容不是有效的 JSON。\n"
            + "请选择由本插件导出的 .json、PaperTodo 主数据文件 data.json；" +
            "如果是 Markdown 笔记，请确认文件扩展名为 .md 后重新导入。"
    };

    private static ImportPlan UnsupportedPlan() => new()
    {
        FatalError =
            "该文件不是可导入的数据文件。\n"
            + "请选择由本插件导出的 JSON、PaperTodo 主数据文件 data.json，或 Markdown 文件。"
    };

    /// <summary>校验内存中的导出文档并生成导入计划（JSON 与 Markdown 共用）。</summary>
    private static ImportPlan ParseDocument(ExportDocument document, bool supportsTodoMetadata = true)
    {
        var plan = new ImportPlan
        {
            SupportsReminders = supportsTodoMetadata,
            SupportsPaperLinks = supportsTodoMetadata
        };

        // 显式 null 或数组内混入 null 都要过滤，避免后续访问元素时抛空引用。
        document.Papers = document.Papers?.OfType<ExportPaper>().ToList() ?? [];

        if (!string.Equals(document.Format, ExportDocument.FormatName, StringComparison.OrdinalIgnoreCase))
        {
            plan.FatalError =
                "该文件不是本插件导出的数据。\n"
                + "请确认选择的是通过“导出数据”生成的 JSON 文件，且文件内容未被其他程序改写。";
            return plan;
        }

        if (document.SchemaVersion < ExportDocument.MinimumSupportedSchemaVersion ||
            document.SchemaVersion > ExportDocument.CurrentSchemaVersion)
        {
            plan.FatalError = document.SchemaVersion > ExportDocument.CurrentSchemaVersion
                ? $"文件由更新版本的插件导出（schemaVersion={document.SchemaVersion}），请先升级插件。"
                : $"不支持的文件版本（schemaVersion={document.SchemaVersion}）。";
            return plan;
        }

        if (document.Papers.Count == 0)
        {
            plan.FatalError = "文件中没有任何纸片。";
            return plan;
        }

        if (document.Papers.Count > MaxPapers)
        {
            plan.FatalError = $"文件包含 {document.Papers.Count} 张纸片，超过单个工作区 {MaxPapers} 张上限。";
            return plan;
        }

        // 先确认 sourceId 唯一，重复会让同文件内的链接映射产生歧义。
        var sourceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var paper in document.Papers)
        {
            if (string.IsNullOrWhiteSpace(paper.SourceId))
            {
                continue;
            }

            if (!sourceIds.Add(paper.SourceId))
            {
                plan.FatalError = $"文件包含重复的 sourceId：{paper.SourceId}。";
                return plan;
            }
        }

        var now = DateTimeOffset.Now;

        foreach (var source in document.Papers)
        {
            var type = (source.Type ?? "").Trim().ToLowerInvariant();

            // 只保留结构完整的待办条目，并把显式 null 文本归一为空串。
            source.Todos = source.Todos?.OfType<ExportTodo>().ToList();
            foreach (var todo in source.Todos ?? [])
            {
                todo.Text ??= "";
            }

            if (type is not ("todo" or "note"))
            {
                plan.Warnings.Add($"跳过未知类型“{source.Type}”的纸片“{source.Title}”。");
                continue;
            }

            if (type == "note" && source.Todos is { Count: > 0 })
            {
                plan.Warnings.Add($"跳过笔记“{source.Title}”：笔记不能包含待办。");
                continue;
            }

            if (type == "todo" && !string.IsNullOrEmpty(source.Content))
            {
                plan.Warnings.Add($"跳过待办纸片“{source.Title}”：待办纸片不能包含笔记正文。");
                continue;
            }

            if (type == "note" && (source.Content?.Length ?? 0) > MaxNoteChars)
            {
                plan.Warnings.Add($"跳过笔记“{source.Title}”：正文超过 {MaxNoteChars} 字符上限。");
                continue;
            }

            if (type == "todo")
            {
                source.Todos ??= [];
                if (source.Todos.Count > MaxTodosPerPaper)
                {
                    plan.Warnings.Add($"跳过待办纸片“{source.Title}”：待办数量 {source.Todos.Count} 超过 {MaxTodosPerPaper} 条上限。");
                    continue;
                }

                if (source.Todos.Any(t => (t.Text?.Length ?? 0) > MaxTodoTextChars))
                {
                    plan.Warnings.Add($"跳过待办纸片“{source.Title}”：存在超过 {MaxTodoTextChars} 字符的待办。");
                    continue;
                }

                source.Todos.RemoveAll(t => string.IsNullOrWhiteSpace(t.Text));
                plan.TodoCount += source.Todos.Count;

                foreach (var todo in source.Todos)
                {
                    if (!string.IsNullOrWhiteSpace(todo.LinkedSourcePaperId) &&
                        !sourceIds.Contains(todo.LinkedSourcePaperId))
                    {
                        plan.UnresolvedLinks++;
                    }

                    if (!string.IsNullOrWhiteSpace(todo.LinkedPath))
                    {
                        plan.PathLinkCount++;
                    }

                    if (!todo.Done && todo.ReminderAt is { } reminder && reminder <= now)
                    {
                        plan.DroppedReminders++;
                    }
                }
            }

            var normalizedTitle = TitleSanitizer.Normalize(source.Title);
            string? safeTitle = normalizedTitle;
            if (normalizedTitle.Length == 0)
            {
                safeTitle = null;
                plan.Warnings.Add($"纸片“{source.Title}”标题为空，导入时将使用默认标题。");
            }
            else if (TitleSanitizer.DisplayWidth(normalizedTitle) > TitleSanitizer.PreTruncateDisplayWidth)
            {
                safeTitle = TitleSanitizer.TruncateByDisplayWidth(normalizedTitle, TitleSanitizer.PreTruncateDisplayWidth);
                plan.TruncatedTitles++;
                plan.Warnings.Add($"纸片标题“{normalizedTitle}”过长，将截断为“{safeTitle}”。");
            }

            if (type == "note")
            {
                var imageReferences = ImageReferenceScanner.Count(source.Content);
                if (imageReferences > 0)
                {
                    plan.NotesWithImages++;
                    plan.ImageReferenceCount += imageReferences;
                }

                // 旧版 Markdown 导出会把图片引用替换为缺失标记；识别出来以便在更新时拦截。
                if (ImageReferenceScanner.ContainsMissingLabel(
                        source.Content,
                        MarkdownExportService.MissingImageLabel))
                {
                    plan.LossyImageNotes++;
                }
            }

            plan.Papers.Add(new ImportPaper
            {
                Source = source,
                SafeTitle = safeTitle
            });
        }

        if (plan.Papers.Count == 0)
        {
            plan.FatalError = "文件中没有可导入的纸片。";
        }

        if (plan.LossyImageNotes > 0)
        {
            plan.Warnings.Add(
                $"{plan.LossyImageNotes} 张笔记的正文含“图片缺失”标记（旧版 Markdown 导出的结果）。" +
                "更新这些纸片会覆盖本地图片引用，已禁止更新，建议改用 JSON 文件或选择新建。");
        }

        return plan;
    }
}
