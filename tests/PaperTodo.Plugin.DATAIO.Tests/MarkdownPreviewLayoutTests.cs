using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace PaperTodo.Plugin.DataIo.Tests;

/// <summary>
/// 预览排版：正文、图片、正文、图片、正文应按原顺序交错渲染，
/// 图片独占一行并按其 width 说明计算显示宽度。
/// </summary>
public sealed class MarkdownPreviewLayoutTests
{
    [Fact]
    public void Interleaved_text_and_images_keep_their_order()
    {
        RunSta(() =>
        {
            var png = CreatePng(4, 2);
            const double contentWidth = 400;
            var palette = new PreviewPalette(
                Brushes.Black,
                Brushes.Gray,
                Brushes.LightGray,
                Brushes.Blue,
                Brushes.Transparent,
                1.0,
                new FontFamily("Segoe UI"),
                new FontFamily("Consolas"),
                contentWidth);

            var body = string.Join("\n", new[]
            {
                "第一段文字",
                "",
                "![image|100%](i:001)",
                "",
                "第二段文字",
                "",
                "![image|50%](i:002)",
                "",
                "第三段文字"
            });

            var host = new StackPanel();
            MarkdownPreview.Render(
                body,
                host,
                palette,
                new Dictionary<string, PreviewImage>(StringComparer.Ordinal)
                {
                    ["i:001"] = new PreviewImage("image/png", png),
                    ["i:002"] = new PreviewImage("image/png", png)
                });

            Assert.Collection(
                host.Children.Cast<UIElement>().ToList(),
                child => Assert.IsType<TextBlock>(child),
                child => AssertImageWidth(child, contentWidth),
                child => Assert.IsType<TextBlock>(child),
                child => AssertImageWidth(child, contentWidth / 2),
                child => Assert.IsType<TextBlock>(child));
        });
    }

    private static void AssertImageWidth(object child, double expectedWidth)
    {
        var border = Assert.IsType<Border>(child);
        var image = Assert.IsType<Image>(border.Child);
        Assert.Equal(expectedWidth, image.Width, precision: 1);
        Assert.NotNull(image.Source);
    }

    private static byte[] CreatePng(int width, int height)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = 0x20;
            pixels[index + 1] = 0x80;
            pixels[index + 2] = 0xE0;
            pixels[index + 3] = 0xFF;
        }

        var bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            stride);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
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
