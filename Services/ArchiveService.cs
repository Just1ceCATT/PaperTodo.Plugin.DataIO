using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace PaperTodo.Plugin.DataIo;

/// <summary>
/// PaperTodo 归档读写。归档为 zip，内部固定包含 manifest.json（导出文档）；
/// 同时兼容直接读写 .json 文件，便于手工查看和向后兼容。
/// </summary>
internal static class ArchiveService
{
    public const string ManifestEntryName = "manifest.json";

    public static void Write(string path, ExportDocument document)
    {
        var json = JsonSerializer.Serialize(document, ExportJson.Options);

        // 非 .zip 一律按纯 JSON 写出。
        if (!string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase))
        {
            JsonFiles.WriteAllTextAtomic(path, json);
            return;
        }

        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry(ManifestEntryName, CompressionLevel.Optimal);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                writer.Write(json);
            }

            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                try
                {
                    File.Delete(temp);
                }
                catch (IOException)
                {
                    // 清理失败不影响主流程。
                }
                catch (UnauthorizedAccessException)
                {
                    // 清理失败不影响主流程。
                }
            }
        }
    }

    /// <summary>读取 zip 中的 manifest.json；非 zip 文件按纯 JSON 读取。</summary>
    public static string ReadPayload(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new FileNotFoundException("导入文件不存在。", path);
        }

        if (info.Length > JsonFiles.MaxImportBytes)
        {
            throw new InvalidDataException("导入文件超过 64 MiB 上限。");
        }

        if (!IsZip(path))
        {
            return JsonFiles.ReadAllText(path);
        }

        using var stream = File.OpenRead(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var entry = archive.GetEntry(ManifestEntryName)
            ?? throw new InvalidDataException($"压缩包缺少 {ManifestEntryName}。");
        if (entry.Length > JsonFiles.MaxImportBytes)
        {
            throw new InvalidDataException("压缩包清单超过 64 MiB 上限。");
        }

        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static bool IsZip(string path)
    {
        if (string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        using var stream = File.OpenRead(path);
        Span<byte> magic = stackalloc byte[4];
        var read = stream.Read(magic);
        return read == 4 &&
               magic[0] == (byte)'P' &&
               magic[1] == (byte)'K' &&
               magic[2] == 0x03 &&
               magic[3] == 0x04;
    }
}
