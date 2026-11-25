using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PaperTodo.Plugin;
using Xunit;

namespace PaperTodo.Plugin.DataIo.Tests;

/// <summary>超过 8 MiB 的图片按宿主规则压缩后写回。</summary>
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
