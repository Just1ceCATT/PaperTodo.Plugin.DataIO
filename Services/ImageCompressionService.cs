using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PaperTodo.Plugin;

namespace PaperTodo.Plugin.DataIo;

/// <summary>压缩后的图片字节与格式。</summary>
internal sealed record CompressedImage(byte[] Bytes, string Mime, int Width, int Height);

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
    /// 尝试把图片压缩到 8 MiB 以内。失败时返回 false 并给出可直接展示的原因，
    /// 调用方应保留压缩包中的原图并标记为不可写回。
    /// </summary>
    public static bool TryCompress(
        byte[] source,
        string? mime,
        out CompressedImage result,
        out string failureReason)
    {
        result = default!;
        failureReason = "";

        if (source.Length <= MaxImageBytes)
        {
            failureReason = "图片未超过 8 MiB";
            return false;
        }

        if (source.Length > MaxSourceBytes)
        {
            failureReason = "原图超过 32 MiB，无法自动压缩";
            return false;
        }

        var normalizedMime = string.IsNullOrWhiteSpace(mime) ? "image/png" : mime.Trim();
        if (normalizedMime is "image/gif" or "image/tiff")
        {
            failureReason = "GIF/TIFF 压缩会丢失动画或额外帧";
            return false;
        }

        if (!TryReadBitmap(source, out var frame, out var width, out var height))
        {
            failureReason = "无法解码图片";
            return false;
        }

        if (width > MaxStoredDimension || height > MaxStoredDimension)
        {
            failureReason = $"图片尺寸超过 {MaxStoredDimension} 像素";
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

    private static bool TryReadBitmap(
        byte[] bytes,
        out BitmapFrame frame,
        out int width,
        out int height)
    {
        frame = null!;
        width = 0;
        height = 0;
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            frame = decoder.Frames[0];
            frame.Freeze();
            width = frame.PixelWidth;
            height = frame.PixelHeight;
            return width > 0 && height > 0;
        }
        catch (Exception)
        {
            return false;
        }
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
        if (source.Format != PixelFormats.Bgr24 && source.Format != PixelFormats.Bgr32)
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
