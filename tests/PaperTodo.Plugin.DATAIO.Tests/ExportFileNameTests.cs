using Xunit;

namespace PaperTodo.Plugin.DataIo.Tests;

/// <summary>导出文件默认命名的紧凑格式。</summary>
public sealed class ExportFileNameTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 23, 14, 35, 0, TimeSpan.FromHours(8));

    [Fact]
    public void Json_name_contains_short_time_and_paper_count()
    {
        var name = ExportFileName.Build(At, 3, markdown: false);

        Assert.Equal("pt-261123-1435-3张.json", name);
    }

    [Fact]
    public void Markdown_name_uses_md_extension()
    {
        var name = ExportFileName.Build(At, 2, markdown: true);

        Assert.Equal("pt-261123-1435-2张.md", name);
    }

    [Fact]
    public void Name_is_shorter_than_legacy_default()
    {
        var name = ExportFileName.Build(At, 3, markdown: false);
        const string legacy = "papertodo-export-20261123-143500.json";

        Assert.True(name.Length < legacy.Length - 8);
    }

    [Fact]
    public void Negative_count_is_clamped()
    {
        var name = ExportFileName.Build(At, -1, markdown: false);

        Assert.Equal("pt-261123-1435-0张.json", name);
    }

    [Fact]
    public void Zip_extension_replaces_plain_extension()
    {
        Assert.Equal(
            @"C:\导出\pt-261123-1435-3张.zip",
            ExportFileName.EnsureZipExtension(@"C:\导出\pt-261123-1435-3张.json"));
        Assert.Equal(
            @"C:\导出\pt-261123-1435-3张.zip",
            ExportFileName.EnsureZipExtension(@"C:\导出\pt-261123-1435-3张.md"));
    }

    [Fact]
    public void Zip_extension_is_kept_when_already_zip()
    {
        Assert.Equal(
            @"C:\导出\backup.ZIP",
            ExportFileName.EnsureZipExtension(@"C:\导出\backup.ZIP"));
    }

    [Fact]
    public void Zip_name_uses_zip_extension_when_images_are_referenced()
    {
        Assert.Equal(
            "pt-261123-1435-3张.zip",
            ExportFileName.Build(At, 3, markdown: false, zip: true));
        Assert.Equal(
            "pt-261123-1435-2张.zip",
            ExportFileName.Build(At, 2, markdown: true, zip: true));
    }

    [Fact]
    public void Single_file_extension_is_restored_for_plain_exports()
    {
        Assert.Equal(
            @"C:\导出\pt-261123-1435-3张.json",
            ExportFileName.EnsureSingleFileExtension(@"C:\导出\pt-261123-1435-3张.zip", markdown: false));
        Assert.Equal(
            @"C:\导出\pt-261123-1435-3张.md",
            ExportFileName.EnsureSingleFileExtension(@"C:\导出\pt-261123-1435-3张.json", markdown: true));
        Assert.Equal(
            @"C:\导出\pt-261123-1435-3张.MD",
            ExportFileName.EnsureSingleFileExtension(@"C:\导出\pt-261123-1435-3张.MD", markdown: true));
    }
}
