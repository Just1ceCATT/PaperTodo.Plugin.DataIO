using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PaperTodo.Plugin.DataIo;

/// <summary>
/// 含图片的导出包：正文与 images/ 下的原始图片文件一起写入 zip。
/// 写包全程只使用内存与临时文件，最终原子替换目标路径。
/// </summary>
internal static class ExportPackageService
{
    public const string JsonEntryName = "dataio-export.json";
    public const string MarkdownEntryName = "dataio-export.md";
    public const string MarkdownManifestEntryName = "dataio-images.json";

    private const string ImagesDirectory = "images";

    /// <summary>生成压缩包内的图片路径；来源 ID 与图片 ID 都做安全化处理，路径统一使用正斜杠。</summary>
    public static string BuildImageEntry(string paperSourceId, string imageId, string mime) =>
        $"{ImagesDirectory}/{SafeSegment(paperSourceId)}/{SafeSegment(imageId)}{ExtensionFromMime(mime)}";

    /// <summary>JSON 压缩包：正文保持 i: 引用，清单写在 ExportDocument.Images 中。</summary>
    public static void WriteJsonZip(
        string path,
        ExportDocument document,
        IReadOnlyList<CapturedExportImage> images)
    {
        var json = JsonSerializer.Serialize(document, ExportJson.Options);
        EnsureTextFits(json, "导出 JSON");
        JsonFiles.WriteBytesAtomic(path, stream =>
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
            AddText(
                archive,
                JsonEntryName,
                json,
                CompressionLevel.Optimal);
            foreach (var image in images)
            {
                AddBytes(archive, image.File, image.Bytes);
            }
        });
    }

    /// <summary>
    /// Markdown 压缩包：正文里的 i: 引用改写成 images/ 相对链接，
    /// 另放一份清单供导入时映射回 PaperTodo 图片。
    /// </summary>
    public static void WriteMarkdownZip(
        string path,
        ExportDocument document,
        IReadOnlyList<CapturedExportImage> images)
    {
        var links = images.ToDictionary(
            image => (image.PaperSourceId, image.ImageId),
            image => image.File);
        var markdown = MarkdownExportService.Build(
            document,
            (paperSourceId, imageId) =>
                links.TryGetValue((paperSourceId, imageId), out var file) ? file : null);
        EnsureTextFits(markdown, "导出 Markdown");

        var manifest = new ExportImageManifest
        {
            Images = images.Select(ToManifest).ToList()
        };

        JsonFiles.WriteBytesAtomic(path, stream =>
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
            AddText(archive, MarkdownEntryName, markdown, CompressionLevel.Optimal);
            AddText(
                archive,
                MarkdownManifestEntryName,
                JsonSerializer.Serialize(manifest, ExportJson.Options),
                CompressionLevel.Optimal);
            foreach (var image in images)
            {
                AddBytes(archive, image.File, image.Bytes);
            }
        });
    }

    public static ExportImage ToManifest(CapturedExportImage image) => new()
    {
        PaperSourceId = image.PaperSourceId,
        ImageId = image.ImageId,
        File = image.File,
        Mime = image.Mime,
        ByteLength = image.Bytes.Length,
        Sha256 = image.Sha256,
        Restorable = image.Restorable
    };

    private static void AddText(
        ZipArchive archive,
        string name,
        string text,
        CompressionLevel level)
    {
        var entry = archive.CreateEntry(name, level);
        using var stream = entry.Open();
        stream.Write(Encoding.UTF8.GetBytes(text));
    }

    private static void AddBytes(ZipArchive archive, string name, byte[] bytes)
    {
        // 图片本身已经压缩过，再压缩只会增加导出耗时。
        var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
        using var stream = entry.Open();
        stream.Write(bytes);
    }

    /// <summary>正文条目超过导入端单文件上限时，导出产物无法被本插件读回，必须提前拒绝。</summary>
    private static void EnsureTextFits(string text, string what)
    {
        if (Encoding.UTF8.GetByteCount(text) > JsonFiles.MaxImportBytes)
        {
            throw new InvalidOperationException(
                $"{what}超过 64 MiB 上限，生成的文件将无法被本插件重新导入；请减少纸片数量后重试。");
        }
    }

    /// <summary>把纸片 ID 限制在安全字符内；发生替换时追加短哈希，避免不同 ID 落到同一目录。</summary>
    private static string SafeSegment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "paper";
        }

        var safe = new StringBuilder(value.Length);
        var replaced = false;
        foreach (var character in value)
        {
            if (char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
            {
                safe.Append(character);
            }
            else
            {
                safe.Append('_');
                replaced = true;
            }
        }

        if (!replaced)
        {
            return safe.ToString();
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant()[..8];
        return $"{safe}-{hash}";
    }

    private static string ExtensionFromMime(string mime) => mime switch
    {
        "image/jpeg" => ".jpg",
        "image/gif" => ".gif",
        "image/bmp" => ".bmp",
        "image/tiff" => ".tif",
        "image/webp" => ".webp",
        _ => ".png"
    };
}

/// <summary>导出过程中读取到的一张图片；File 已按压缩包布局生成。</summary>
internal sealed record CapturedExportImage(
    string PaperSourceId,
    string ImageId,
    string File,
    string Mime,
    byte[] Bytes,
    bool Restorable)
{
    public string Sha256 { get; } =
        Convert.ToHexString(SHA256.HashData(Bytes)).ToLowerInvariant();
}
