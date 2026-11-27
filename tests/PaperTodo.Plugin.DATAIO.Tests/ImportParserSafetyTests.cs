using System.Diagnostics;
using System.Text;
using Xunit;

namespace PaperTodo.Plugin.DataIo.Tests;

/// <summary>导入解析的资源上限与编码校验。</summary>
public sealed class ImportParserSafetyTests
{
    [Fact]
    public void Markdown_continuation_lines_parse_in_linear_time()
    {
        // 旧实现用 `text += ...` 拼接续行，行数翻倍耗时约四倍；
        // 5 万行续行在旧实现下约 0.5 秒，这里用更宽的阈值防 CI 抖动。
        var builder = new StringBuilder("- [ ] 标题\n");
        for (var index = 0; index < 50_000; index++)
        {
            builder.Append("    续行\n");
        }

        var stopwatch = Stopwatch.StartNew();
        var document = MarkdownImportParser.Parse(builder.ToString(), "x.md");
        stopwatch.Stop();

        var paper = Assert.Single(document.Papers);
        Assert.StartsWith("标题", paper.Todos![0].Text);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(2),
            $"解析耗时 {stopwatch.Elapsed.TotalSeconds:F1} 秒，疑似退化为二次复杂度。");
    }

    [Fact]
    public void Markdown_with_too_many_lines_is_rejected_before_split()
    {
        var content = new string('\n', MarkdownImportParser.MaxMarkdownLines + 1);

        Assert.Throws<ImportFileLimitException>(
            () => MarkdownImportParser.Parse(content, "huge.md"));
    }

    [Fact]
    public void Markdown_with_too_many_papers_is_rejected_early()
    {
        var builder = new StringBuilder();
        for (var index = 0; index <= ImportPlanParser.MaxPapers; index++)
        {
            builder.Append("<!-- papertodo-dataio:v2 type=note sourceId=s")
                .Append(index)
                .Append(" -->\n# 标题\n正文\n\n");
        }

        Assert.Throws<ImportFileLimitException>(
            () => MarkdownImportParser.Parse(builder.ToString(), "many.md"));
    }

    [Fact]
    public void Non_utf8_bytes_are_rejected_instead_of_mojibake()
    {
        // “中文”的 GBK 字节；宽松 UTF-8 会把它们替换成 U+FFFD 后继续导入。
        byte[] gbk = [0xD6, 0xD0, 0xCE, 0xC4];

        Assert.Throws<ImportFileEncodingException>(
            () => JsonFiles.DecodeText(gbk, "data.json"));
    }

    [Fact]
    public void Utf8_bom_is_stripped()
    {
        byte[] bytes = [0xEF, 0xBB, 0xBF, 0xE4, 0xB8, 0xAD];

        Assert.Equal("中", JsonFiles.DecodeText(bytes, "x.md"));
    }

    [Fact]
    public void Json_array_root_is_rejected_without_building_dom()
    {
        var plan = ImportPlanParser.Parse("[0,0,0,0]");

        Assert.NotNull(plan.FatalError);
        Assert.Contains("不是可导入", plan.FatalError);
    }
}
