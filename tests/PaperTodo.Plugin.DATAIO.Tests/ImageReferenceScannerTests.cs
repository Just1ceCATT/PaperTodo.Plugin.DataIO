using System.Diagnostics;
using Xunit;

namespace PaperTodo.Plugin.DataIo.Tests;

/// <summary>图片引用扫描的兼容性与抗退化行为。</summary>
public sealed class ImageReferenceScannerTests
{
    [Fact]
    public void Label_may_contain_brackets_like_host_parser()
    {
        var references = ImageReferenceScanner.Enumerate("![a]b](i:123456)");

        var reference = Assert.Single(references);
        Assert.Equal("123456", reference.ImageId);
        Assert.Equal(0, reference.Start);
        Assert.Equal("![a]b](i:123456)".Length, reference.Length);
    }

    [Fact]
    public void Label_cannot_cross_line_break()
    {
        // 跨行的 “](” 不构成引用，PaperTodo 同样按行解析。
        Assert.Empty(ImageReferenceScanner.Enumerate("![a\nb](i:123456)"));
    }

    [Fact]
    public void Nine_digit_ids_are_not_references()
    {
        Assert.Empty(ImageReferenceScanner.Enumerate("![a](i:123456789)"));
    }

    [Fact]
    public void Unclosed_brackets_scan_in_linear_time()
    {
        // 旧实现（惰性正则）在 10 万个 “![” 且无 “](” 时会二次回溯，耗时约 30 秒。
        var content = string.Concat(Enumerable.Repeat("![", 50_000));

        var stopwatch = Stopwatch.StartNew();
        Assert.Equal(0, ImageReferenceScanner.Count(content));
        Assert.Empty(ImageReferenceScanner.Enumerate(content));
        stopwatch.Stop();

        // 线性扫描应在毫秒级完成；放宽到 2 秒以兼容 CI 抖动。
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(2),
            $"扫描耗时 {stopwatch.Elapsed.TotalSeconds:F1} 秒，疑似退化为二次复杂度。");
    }

    [Fact]
    public void Many_starts_sharing_invalid_close_scan_in_linear_time()
    {
        // 大量 “![” 共用同一个非法 “](”，必须记忆化失败结果，不能每个起点重试。
        var content = string.Concat(Enumerable.Repeat("![", 50_000)) + "](not-an-image";

        var stopwatch = Stopwatch.StartNew();
        Assert.Empty(ImageReferenceScanner.Enumerate(content));
        stopwatch.Stop();

        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(2),
            $"扫描耗时 {stopwatch.Elapsed.TotalSeconds:F1} 秒，疑似重复解析同一个地址。");
    }
}
