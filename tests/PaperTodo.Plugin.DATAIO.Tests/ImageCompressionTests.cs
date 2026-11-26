using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PaperTodo.Plugin;
using Xunit;

namespace PaperTodo.Plugin.DataIo.Tests;

/// <summary>超过 8 MiB 的图片按 PaperTodo 规则压缩后写回。</summary>
public sealed class ImageCompressionTests
{
    [Fact]
    public void Large_opaque_png_is_compressed_and_resized()
    {
        RunSta(() =>
        {
            var source = CreateNoisePng(2600, 2600, PixelFormats.Bgr24);
            Assert.True(source.Length > ImageCompressionService.MaxImageBytes);

            Assert.True(ImageCompressionService.TryCompress(
                source,
                "image/png",
                out var compressed,
                out var reason), reason);

            Assert.True(compressed.Bytes.Length <= ImageCompressionService.MaxImageBytes);
            Assert.True(Math.Max(compressed.Width, compressed.Height) <= 2048);
            Assert.Equal("image/jpeg", compressed.Mime);
        });
    }

    [Fact]
    public void Oversized_dimensions_are_rejected_before_full_decode()
    {
        RunSta(() =>
        {
            var source = CreateNoisePng(4200, 1200, PixelFormats.Bgr24);
            Assert.True(source.Length > ImageCompressionService.MaxImageBytes);

            Assert.False(ImageCompressionService.TryCompress(
                source,
                "image/png",
                out _,
                out var reason));
            Assert.Contains("4096", reason);
        });
    }

    [Theory]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A }, true)]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, true)]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 }, true)]
    [InlineData(new byte[] { 0x3C, 0x68, 0x74, 0x6D, 0x6C, 0x3E }, false)]
    [InlineData(new byte[] { 0x4D, 0x5A, 0x90, 0x00 }, false)]
    public void Signature_check_rejects_non_images(byte[] bytes, bool expected)
    {
        Assert.Equal(expected, ImageCompressionService.HasRecognizedImageSignature(bytes));
    }

    [Fact]
    public async Task Compression_runs_on_worker_thread()
    {
        // 导入解析走 Task.Run（线程池/MTA），压缩不能依赖 STA。
        var source = CreateNoisePng(1800, 1800, PixelFormats.Bgr24);
        var outcome = await Task.Run(() =>
        {
            var ok = ImageCompressionService.TryCompress(
                source,
                "image/png",
                out var compressed,
                out var reason);
            return (Ok: ok, Compressed: compressed, Reason: reason);
        });

        Assert.True(outcome.Ok, outcome.Reason);
        Assert.True(outcome.Compressed.Bytes.Length <= ImageCompressionService.MaxImageBytes);
    }

    [Fact]
    public void Gif_source_is_not_reencoded()
    {
        var source = new byte[ImageCompressionService.MaxImageBytes + 1];

        Assert.False(ImageCompressionService.TryCompress(
            source,
            "image/gif",
            out _,
            out var reason));
        Assert.Contains("GIF", reason);
    }

    [Fact]
    public void Small_image_is_not_compressed()
    {
        RunSta(() =>
        {
            var source = CreateNoisePng(64, 64, PixelFormats.Bgr24);

            Assert.False(ImageCompressionService.TryCompress(
                source,
                "image/png",
                out _,
                out var reason));
            Assert.Contains("8 MiB", reason);
        });
    }

    [Fact]
    public void Oversized_image_is_compressed_during_package_import()
    {
        RunSta(() => WithTempDirectory(directory =>
        {
            // 压缩包读取上限是 16 MiB；用 1800² 的随机 PNG 覆盖 8–16 MiB 的真实区间。
            var source = CreateNoisePng(1800, 1800, PixelFormats.Bgr24);
            Assert.True(source.Length > ImageCompressionService.MaxImageBytes);
            Assert.True(source.Length < IPaperNoteAssetsApi.MaximumImageBytes);

            var image = new CapturedExportImage(
                "p1",
                "123456",
                ExportPackageService.BuildImageEntry("p1", "123456", "image/png"),
                "image/png",
                source,
                Restorable: false);

            var document = new ExportDocument
            {
                SchemaVersion = ExportDocument.CurrentSchemaVersion,
                Papers =
                [
                    new ExportPaper
                    {
                        SourceId = "p1",
                        Type = "note",
                        Title = "大图笔记",
                        ContentAvailable = true,
                        Content = "![image|100%](i:123456)"
                    }
                ],
                Images = [ExportPackageService.ToManifest(image)]
            };

            var path = Path.Combine(directory, "large.zip");
            ExportPackageService.WriteJsonZip(path, document, [image]);

            var plan = ImportPlanParser.ParseFile(path);

            Assert.Null(plan.FatalError);
            Assert.Equal(1, plan.CompressedImageCount);
            Assert.Equal(1, plan.RestorableImageCount);
            Assert.Equal(0, plan.UnrestorableImageCount);
            Assert.Equal(0, plan.CompressionFailedImageCount);

            var asset = Assert.Single(Assert.Single(plan.Papers).Images);
            Assert.True(asset.Restorable);
            Assert.True(asset.Bytes.Length <= ImageCompressionService.MaxImageBytes);
            Assert.Equal("image/jpeg", asset.Mime);
        }));
    }

    [Fact]
    public void Wide_image_under_8MiB_is_compressed_during_package_import()
    {
        RunSta(() => WithTempDirectory(directory =>
        {
            // 复现“重复导入图片膨胀”的根因：图片小于 8 MiB，但 PaperTodo 会因最长边超过 2048
            // 自动重编码。插件必须同样处理，写回的字节才是确定的。
            var source = CreateNoisePng(2600, 120, PixelFormats.Bgr24);
            Assert.True(source.Length < ImageCompressionService.MaxImageBytes);

            var image = new CapturedExportImage(
                "p1",
                "123456",
                ExportPackageService.BuildImageEntry("p1", "123456", "image/png"),
                "image/png",
                source,
                Restorable: true);

            var document = new ExportDocument
            {
                SchemaVersion = ExportDocument.CurrentSchemaVersion,
                Papers =
                [
                    new ExportPaper
                    {
                        SourceId = "p1",
                        Type = "note",
                        Title = "宽图笔记",
                        ContentAvailable = true,
                        Content = "![image|100%](i:123456)"
                    }
                ],
                Images = [ExportPackageService.ToManifest(image)]
            };

            var path = Path.Combine(directory, "wide.zip");
            ExportPackageService.WriteJsonZip(path, document, [image]);

            var plan = ImportPlanParser.ParseFile(path);

            Assert.Null(plan.FatalError);
            Assert.Equal(1, plan.CompressedImageCount);
            Assert.Equal(1, plan.RestorableImageCount);

            var asset = Assert.Single(Assert.Single(plan.Papers).Images);
            Assert.True(asset.Restorable);
            Assert.NotEqual(source.Length, asset.Bytes.Length);
            Assert.True(asset.Bytes.Length < source.Length);
        }));
    }

    [Fact]
    public void Dimension_triggered_host_compression_is_deterministic()
    {
        RunSta(() =>
        {
            // 小于 8 MiB、但最长边超过 2048：PaperTodo 会自动重编码，插件必须给出确定的结果，
            // 否则再次导入时字节对不上，无法复用图片。
            var source = CreateNoisePng(2600, 120, PixelFormats.Bgr24);
            Assert.True(source.Length < ImageCompressionService.MaxImageBytes);

            Assert.True(ImageCompressionService.TryPrepareForHost(
                source,
                "image/png",
                out var first,
                out var reason), reason);
            Assert.True(first.Compressed);
            Assert.True(Math.Max(first.Width, first.Height) <= 2048);
            Assert.True(first.Bytes.Length <= ImageCompressionService.MaxImageBytes);

            Assert.True(ImageCompressionService.TryPrepareForHost(
                source,
                "image/png",
                out var second,
                out _));
            Assert.Equal(first.Bytes, second.Bytes);
        });
    }

    [Fact]
    public void Small_image_passes_through_unchanged()
    {
        RunSta(() =>
        {
            var source = CreateNoisePng(64, 64, PixelFormats.Bgr24);

            Assert.True(ImageCompressionService.TryPrepareForHost(
                source,
                "image/png",
                out var prepared,
                out var reason), reason);
            Assert.False(prepared.Compressed);
            Assert.Same(source, prepared.Bytes);
        });
    }

    [Fact]
    public void Gif_that_host_would_reencode_is_rejected()
    {
        RunSta(() =>
        {
            var source = CreateGif(2600, 100);

            Assert.False(ImageCompressionService.TryPrepareForHost(
                source,
                "image/gif",
                out _,
                out var reason));
            Assert.Contains("GIF", reason);
        });
    }

    [Fact]
    public void Oversized_dimensions_are_rejected_by_host_preparation()
    {
        RunSta(() =>
        {
            var source = CreateNoisePng(4200, 120, PixelFormats.Bgr24);

            Assert.False(ImageCompressionService.TryPrepareForHost(
                source,
                "image/png",
                out _,
                out var reason));
            Assert.Contains("4096", reason);
        });
    }

    private static byte[] CreateGif(int width, int height)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        new Random(654321).NextBytes(pixels);

        var bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgr32,
            null,
            pixels,
            stride);
        var encoder = new GifBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static byte[] CreateNoisePng(int width, int height, PixelFormat format)
    {
        var stride = width * (format.BitsPerPixel / 8);
        var pixels = new byte[stride * height];
        new Random(123456).NextBytes(pixels);

        var bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            format,
            null,
            pixels,
            stride);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
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

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
