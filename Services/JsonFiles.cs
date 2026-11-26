using System.IO;
using System.Text;

namespace PaperTodo.Plugin.DataIo;

/// <summary>导入导出文件的读写工具，包含大小限制与原子替换。</summary>
internal static class JsonFiles
{
    /// <summary>导入文件大小上限，避免一次读入超大文件拖垮宿主。</summary>
    public const long MaxImportBytes = 64L * 1024 * 1024;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static string ReadAllText(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new FileNotFoundException("导入文件不存在。", path);
        }

        if (info.Length > MaxImportBytes)
        {
            throw new InvalidDataException("导入文件超过 64 MiB 上限。");
        }

        // File.ReadAllText 会自动识别并跳过 UTF-8 BOM。
        return File.ReadAllText(path, Encoding.UTF8);
    }

    /// <summary>先写临时文件再原子替换，避免中途失败留下半截文件。</summary>
    public static void WriteAllTextAtomic(string path, string text) =>
        WriteBytesAtomic(path, stream =>
        {
            using var writer = new StreamWriter(stream, Utf8NoBom, leaveOpen: true);
            writer.Write(text);
        });

    /// <summary>以临时文件写入二进制内容（例如压缩包），完成后原子替换目标文件。</summary>
    public static void WriteBytesAtomic(string path, Action<Stream> write)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(
                       temp,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            {
                write(stream);

                // 先把数据刷到磁盘再改名：断电窗口内改完名却没有数据块落盘，
                // 会得到空/半截文件，而旧文件已经被覆盖。
                stream.Flush();
                stream.Flush(flushToDisk: true);
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
}
