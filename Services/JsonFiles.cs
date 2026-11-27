using System.IO;
using System.Text;

namespace PaperTodo.Plugin.DataIo;

/// <summary>导入导出文件的读写工具，包含大小限制与原子替换。</summary>
internal static class JsonFiles
{
    /// <summary>导入文件大小上限，避免一次读入超大文件拖垮 PaperTodo。</summary>
    public const long MaxImportBytes = 64L * 1024 * 1024;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>严格 UTF-8：遇到非法字节直接失败，不能静默替换成 U+FFFD 把乱码当成正文导入。</summary>
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static string ReadAllText(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("导入文件不存在。", path);
        }

        return DecodeText(ReadAllBytesBounded(path), Path.GetFileName(path));
    }

    /// <summary>
    /// 边读边累计并强制执行大小上限：先查 FileInfo.Length 再整体读取存在 TOCTOU，
    /// 检查后文件被写大就会一次性读入超限内容。
    /// </summary>
    private static byte[] ReadAllBytesBounded(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaxImportBytes)
        {
            throw new InvalidDataException("导入文件超过 64 MiB 上限。");
        }

        using var buffer = new MemoryStream((int)Math.Min(stream.Length, MaxImportBytes));
        var chunk = new byte[81920];
        long total = 0;
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            total += read;
            if (total > MaxImportBytes)
            {
                throw new InvalidDataException("导入文件超过 64 MiB 上限。");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// 按 BOM 识别 UTF-8/UTF-16/UTF-32；没有 BOM 时按严格 UTF-8 解码。
    /// 旧实现使用宽松 UTF-8，GBK/Big5 文件会被静默解码成乱码，随后把乱码当作真实内容导入，
    /// 甚至覆盖已有的本地纸片；这里改为明确报错，提示用户改用 UTF-8 保存。
    /// </summary>
    internal static string DecodeText(ReadOnlySpan<byte> bytes, string name)
    {
        if (bytes.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
        {
            return DecodeStrict(StrictUtf8, bytes[3..], name);
        }

        // UTF-16/UTF-32 同样必须是严格解码：宽松解码会把损坏字节替换成 U+FFFD，
        // 结果仍是“可导入”的乱码正文，与严格校验的目标矛盾。
        if (bytes.StartsWith(new byte[] { 0xFF, 0xFE, 0x00, 0x00 }))
        {
            return DecodeStrict(
                new UTF32Encoding(bigEndian: false, byteOrderMark: false, throwOnInvalidCharacters: true),
                bytes[4..],
                name);
        }

        if (bytes.StartsWith(new byte[] { 0x00, 0x00, 0xFE, 0xFF }))
        {
            return DecodeStrict(
                new UTF32Encoding(bigEndian: true, byteOrderMark: false, throwOnInvalidCharacters: true),
                bytes[4..],
                name);
        }

        if (bytes.StartsWith(new byte[] { 0xFF, 0xFE }))
        {
            return DecodeStrict(
                new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true),
                bytes[2..],
                name);
        }

        if (bytes.StartsWith(new byte[] { 0xFE, 0xFF }))
        {
            return DecodeStrict(
                new UnicodeEncoding(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true),
                bytes[2..],
                name);
        }

        return DecodeStrict(StrictUtf8, bytes, name);
    }

    private static string DecodeStrict(Encoding encoding, ReadOnlySpan<byte> bytes, string name)
    {
        string text;
        try
        {
            text = encoding.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw new ImportFileEncodingException(
                $"文件“{name}”的文本编码无效或不是 UTF-8，无法读取；请用 UTF-8 编码重新保存后再导入。");
        }

        // 无 BOM 的 UTF-16LE 纯 ASCII 文本（如 61 00 62 00）也是合法的 UTF-8 字节，
        // 严格解码不会报错，却会把 NUL 当成正文导入，甚至在“更新”时覆盖本地正文。
        // 文本文件不该包含 NUL，出现即按编码错误拒绝。
        if (text.Contains('\0'))
        {
            throw new ImportFileEncodingException(
                $"文件“{name}”包含空字符，可能不是 UTF-8 编码的文本（例如无 BOM 的 UTF-16）；" +
                "请用 UTF-8 重新保存后再导入。");
        }

        return text;
    }

    /// <summary>
    /// 导出内容超过导入端单文件上限时提前拒绝：否则生成的备份文件
    /// 会被本插件自己的导入逻辑拒绝，用户直到需要恢复时才发现。
    /// </summary>
    public static void EnsureFitsImportLimit(string text, string what)
    {
        if (Encoding.UTF8.GetByteCount(text) > MaxImportBytes)
        {
            throw new InvalidOperationException(
                $"{what}超过 64 MiB 上限，生成的文件将无法被本插件重新导入；请减少纸片数量后重试。");
        }
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
