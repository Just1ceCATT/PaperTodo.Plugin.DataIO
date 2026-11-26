using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PaperTodo.Plugin;

namespace PaperTodo.Plugin.DataIo;

/// <summary>压缩后的图片字节与格式。</summary>
internal sealed record CompressedImage(byte[] Bytes, string Mime, int Width, int Height);

/// <summary>按宿主规则预处理后的图片；Compressed 表示是否发生了重编码。</summary>
internal sealed record PreparedHostImage(byte[] Bytes, string Mime, int Width, int Height, bool Compressed);

/// <summary>
/// 与宿主 NoteImageStore 一致的自动压缩规则：
/// 超过 8 MiB 时把最长边缩到 2048，JPEG 保持 JPEG 质量 82；
/// 其他格式先编码 PNG，PNG 仍超限且没有透明通道时改用 JPEG（取更小者）。
/// GIF/TIFF 会丢失动画或额外帧，因此不做压缩。
/// </summary>
internal static class ImageCompressionService
{
    internal const int MaxImageBytes = IPaperNoteAssetsApi.MaximumImageAppendBytes;
    private const int MaxSourceBytes = 32 * 1024 * 1024;
    private const int AutoCompressedDimension = 2048;
    private const int MaxStoredDimension = 4096;
    private const int JpegQuality = 82;

    public static bool RequiresCompression(int byteLength) => byteLength > MaxImageBytes;

    /// <summary>
    /// 宿主实际会触发自动压缩的条件：超过 8 MiB，或任一方向超过 2048 像素。
    /// 插件必须按同一条件预处理图片，否则宿主的重编码会让本地存储字节与文件中的
    /// 原始字节不一致，导致重复导入时无法复用图片、并误判“图片有变化”。
    /// </summary>
    public static bool RequiresHostCompression(int byteLength, int width, int height) =>
        byteLength > MaxImageBytes || width > AutoCompressedDimension || height > AutoCompressedDimension;

    /// <summary>
    /// 预测并生成宿主保存的图片字节：宿主会重编码的图片由插件先按同样规则处理，
    /// 这样写回后的存储字节就是确定的，再次导入时可按字节复用。
    /// 返回 false 表示宿主会拒绝这张图片（尺寸超过 4096、GIF/TIFF 需压缩、压缩失败等）。
    /// </summary>
    public static bool TryPrepareForHost(
        byte[] source,
        string? mime,
        out PreparedHostImage result,
        out string failureReason)
    {
        result = default!;
        failureReason = "";

        if (!TryReadDimensions(source, out var width, out var height))
        {
            // 无法读取尺寸时无法预测宿主行为，按原字节透传，由宿主在写入时决定；
            // 不能因为读不出尺寸就在插件层提前拒绝图片。
            result = new PreparedHostImage(source, NormalizeMime(mime), 0, 0, false);
            return true;
        }

        if (width > MaxStoredDimension || height > MaxStoredDimension)
        {
            failureReason = $"图片尺寸超过 {MaxStoredDimension} 像素";
            return false;
        }

        // 以文件头实际格式为准：清单里的 Mime 由外部文件提供，可能缺失或出错。
        var effectiveMime = DetectMime(source) ?? NormalizeMime(mime);

        if (!RequiresHostCompression(source.Length, width, height))
        {
            result = new PreparedHostImage(source, effectiveMime, width, height, false);
            return true;
        }

        if (source.Length > MaxSourceBytes)
        {
            failureReason = "原图超过 32 MiB，无法自动压缩";
            return false;
        }

        if (effectiveMime is "image/gif" or "image/tiff")
        {
            failureReason = "GIF/TIFF 压缩会丢失动画或额外帧";
            return false;
        }

        if (!TryCompressCore(source, effectiveMime, out var compressed, out failureReason))
        {
            return false;
        }

        result = new PreparedHostImage(
            compressed.Bytes,
            compressed.Mime,
            compressed.Width,
            compressed.Height,
            true);
        return true;
    }

    private static string NormalizeMime(string? mime) =>
        string.IsNullOrWhiteSpace(mime) ? "image/png" : mime.Trim();

    /// <summary>按文件头判断实际图片格式；用于替代不可信的外部 Mime 声明。</summary>
    private static string? DetectMime(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 4)
        {
            return null;
        }

        if (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
        {
            return "image/png";
        }

        if (bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return "image/jpeg";
        }

        if (bytes[0] == 'G' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == '8')
        {
            return "image/gif";
        }

        if (bytes[0] == 'B' && bytes[1] == 'M')
        {
            return "image/bmp";
        }

        if ((bytes[0] == 'I' && bytes[1] == 'I' && bytes[2] == 0x2A && bytes[3] == 0) ||
            (bytes[0] == 'M' && bytes[1] == 'M' && bytes[2] == 0 && bytes[3] == 0x2A))
        {
            return "image/tiff";
        }

        if (bytes.Length >= 12 &&
            bytes[0] == 'R' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == 'F' &&
            bytes[8] == 'W' && bytes[9] == 'E' && bytes[10] == 'B' && bytes[11] == 'P')
        {
            return "image/webp";
        }

        return null;
    }

    /// <summary>
    /// 尝试把图片压缩到 8 MiB 以内。失败时返回 false 并给出可直接展示的原因，
    /// 调用方应保留压缩包中的原图并标记为不可写回。
    /// </summary>
    public static bool TryCompress(
        byte[] source,
        string? mime,
        out CompressedImage result,
        out string failureReason)
    {
        if (source.Length <= MaxImageBytes)
        {
            result = default!;
            failureReason = "图片未超过 8 MiB";
            return false;
        }

        return TryCompressCore(source, mime, out result, out failureReason);
    }

    /// <summary>核心压缩逻辑，不预设“超过 8 MiB”门槛（宿主按尺寸触发时也会用到）。</summary>
    private static bool TryCompressCore(
        byte[] source,
        string? mime,
        out CompressedImage result,
        out string failureReason)
    {
        result = default!;
        failureReason = "";

        if (source.Length > MaxSourceBytes)
        {
            failureReason = "原图超过 32 MiB，无法自动压缩";
            return false;
        }

        var normalizedMime = NormalizeMime(mime);
        if (normalizedMime is "image/gif" or "image/tiff")
        {
            failureReason = "GIF/TIFF 压缩会丢失动画或额外帧";
            return false;
        }

        // 先只读文件头拿宽高：WPF 的 OnLoad 会立即分配完整像素缓冲，
        // 一个声明 30000×30000 的 PNG 在尺寸检查前就会申请数 GB 内存。
        if (!TryReadDimensions(source, out var width, out var height))
        {
            failureReason = "无法解码图片";
            return false;
        }

        if (width > MaxStoredDimension || height > MaxStoredDimension)
        {
            failureReason = $"图片尺寸超过 {MaxStoredDimension} 像素";
            return false;
        }

        if (!TryReadBitmap(source, out var frame))
        {
            failureReason = "无法解码图片";
            return false;
        }

        try
        {
            var resized = Resize(frame, AutoCompressedDimension);
            byte[] bytes;
            string outputMime;
            if (string.Equals(normalizedMime, "image/jpeg", StringComparison.OrdinalIgnoreCase))
            {
                bytes = EncodeJpeg(resized, JpegQuality);
                outputMime = "image/jpeg";
            }
            else
            {
                var png = EncodePng(resized);
                bytes = png;
                outputMime = "image/png";
                if (png.Length > MaxImageBytes && !HasAlphaChannel(resized))
                {
                    var jpeg = EncodeJpeg(resized, JpegQuality);
                    if (jpeg.Length < png.Length)
                    {
                        bytes = jpeg;
                        outputMime = "image/jpeg";
                    }
                }
            }

            if (bytes.Length >= source.Length)
            {
                failureReason = "压缩后没有变小";
                return false;
            }

            if (bytes.Length > MaxImageBytes)
            {
                failureReason = "压缩后仍超过 8 MiB";
                return false;
            }

            result = new CompressedImage(bytes, outputMime, resized.PixelWidth, resized.PixelHeight);
            return true;
        }
        catch (Exception)
        {
            failureReason = "压缩失败";
            return false;
        }
    }

    /// <summary>只读取文件头中的像素尺寸，不解码像素。</summary>
    private static bool TryReadDimensions(byte[] bytes, out int width, out int height)
    {
        width = 0;
        height = 0;
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.DelayCreation,
                BitmapCacheOption.None);
            if (decoder.Frames.Count == 0)
            {
                return false;
            }

            var frame = decoder.Frames[0];
            width = frame.PixelWidth;
            height = frame.PixelHeight;
            return width > 0 && height > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool TryReadBitmap(byte[] bytes, out BitmapSource frame)
    {
        frame = null!;
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0)
            {
                return false;
            }

            // WPF 解码与重新编码都不会自动应用 EXIF Orientation；
            // 压缩后元数据被丢弃，必须在这里把方向烤进像素，否则竖拍照片会横躺。
            frame = ApplyExifOrientation(decoder.Frames[0]);
            frame.Freeze();
            return frame.PixelWidth > 0 && frame.PixelHeight > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>按 EXIF Orientation（1～8）旋转/翻转解码结果。</summary>
    private static BitmapSource ApplyExifOrientation(BitmapFrame frame)
    {
        var orientation = 1;
        try
        {
            if (frame.Metadata is BitmapMetadata metadata &&
                metadata.GetQuery("System.Photo.Orientation") is ushort value)
            {
                orientation = value;
            }
        }
        catch (Exception)
        {
            orientation = 1;
        }

        var transforms = new TransformGroup();
        switch (orientation)
        {
            case 2:
                transforms.Children.Add(new ScaleTransform(-1, 1));
                break;
            case 3:
                transforms.Children.Add(new RotateTransform(180));
                break;
            case 4:
                transforms.Children.Add(new ScaleTransform(1, -1));
                break;
            case 5:
                transforms.Children.Add(new RotateTransform(90));
                transforms.Children.Add(new ScaleTransform(-1, 1));
                break;
            case 6:
                transforms.Children.Add(new RotateTransform(90));
                break;
            case 7:
                transforms.Children.Add(new RotateTransform(270));
                transforms.Children.Add(new ScaleTransform(-1, 1));
                break;
            case 8:
                transforms.Children.Add(new RotateTransform(270));
                break;
            default:
                return frame;
        }

        var transformed = new TransformedBitmap(frame, transforms);
        transformed.Freeze();
        return transformed;
    }

    /// <summary>
    /// 只按文件头判断是否为常见图片格式。图片清单来自外部文件，
    /// 仅校验大小与清单自报字段会让任意载荷以“图片”身份进入宿主图片库。
    /// </summary>
    public static bool HasRecognizedImageSignature(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 4)
        {
            return false;
        }

        // PNG
        if (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
        {
            return true;
        }

        // JPEG
        if (bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return true;
        }

        // GIF87a / GIF89a
        if (bytes[0] == 'G' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == '8')
        {
            return true;
        }

        // BMP
        if (bytes[0] == 'B' && bytes[1] == 'M')
        {
            return true;
        }

        // TIFF（II*\0 / MM\0*）
        if ((bytes[0] == 'I' && bytes[1] == 'I' && bytes[2] == 0x2A && bytes[3] == 0) ||
            (bytes[0] == 'M' && bytes[1] == 'M' && bytes[2] == 0 && bytes[3] == 0x2A))
        {
            return true;
        }

        // WEBP：RIFF....WEBP
        return bytes.Length >= 12 &&
               bytes[0] == 'R' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == 'F' &&
               bytes[8] == 'W' && bytes[9] == 'E' && bytes[10] == 'B' && bytes[11] == 'P';
    }

    private static BitmapSource Resize(BitmapSource source, int maxDimension)
    {
        var longestEdge = Math.Max(source.PixelWidth, source.PixelHeight);
        BitmapSource bitmap = source;
        if (longestEdge > maxDimension)
        {
            var scale = maxDimension / (double)longestEdge;
            bitmap = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        }

        if (bitmap.CanFreeze)
        {
            bitmap.Freeze();
        }

        return bitmap;
    }

    private static bool HasAlphaChannel(BitmapSource source)
    {
        var format = source.Format;
        if (format == PixelFormats.Bgra32 ||
            format == PixelFormats.Pbgra32 ||
            format == PixelFormats.Rgba64 ||
            format == PixelFormats.Prgba64 ||
            format == PixelFormats.Rgba128Float ||
            format == PixelFormats.Prgba128Float)
        {
            return true;
        }

        return source.Palette?.Colors.Any(color => color.A < byte.MaxValue) == true;
    }

    private static byte[] EncodePng(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        return EncodeBitmap(encoder);
    }

    private static byte[] EncodeJpeg(BitmapSource source, int quality)
    {
        BitmapSource bitmap = source;
        if (source.Format == PixelFormats.Cmyk32)
        {
            // WPF 不做 ICC 色彩管理；CMYK 直接转 Bgr24 在部分解码器上会失败，
            // 先落到 Bgra32 再转，失败时按“压缩失败”处理并保留原图。
            bitmap = new FormatConvertedBitmap(
                new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0),
                PixelFormats.Bgr24,
                null,
                0);
        }
        else if (source.Format != PixelFormats.Bgr24 && source.Format != PixelFormats.Bgr32)
        {
            bitmap = new FormatConvertedBitmap(source, PixelFormats.Bgr24, null, 0);
        }

        var encoder = new JpegBitmapEncoder { QualityLevel = Math.Clamp(quality, 50, 95) };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        return EncodeBitmap(encoder);
    }

    private static byte[] EncodeBitmap(BitmapEncoder encoder)
    {
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
