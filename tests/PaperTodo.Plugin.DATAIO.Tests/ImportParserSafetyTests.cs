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

    [Fact]
    public void Json_array_root_behind_comment_is_rejected_too()
    {
        var plan = ImportPlanParser.Parse("/* 注释 */ [0,0,0]");

        Assert.NotNull(plan.FatalError);
        Assert.Contains("不是可导入", plan.FatalError);
    }

    [Fact]
    public void Carriage_return_only_file_cannot_bypass_line_limit()
    {
        var content = new string('\r', MarkdownImportParser.MaxMarkdownLines + 1);

        Assert.Throws<ImportFileLimitException>(
            () => MarkdownImportParser.Parse(content, "crlf.md"));
    }

    [Fact]
    public void Invalid_utf16_with_bom_is_rejected()
    {
        // UTF-16 LE BOM 后面跟一个孤立的高代理项（不含低代理项）。
        byte[] bytes = [0xFF, 0xFE, 0x00, 0xD8, 0x00, 0x00];

        Assert.Throws<ImportFileEncodingException>(
            () => JsonFiles.DecodeText(bytes, "broken.md"));
    }

    [Fact]
    public void Export_json_with_too_many_declared_papers_is_rejected_early()
    {
        // 只按数组长度预检，不依赖完整反序列化：上百万张空纸片也应在构建对象前被拒。
        var builder = new StringBuilder();
        builder.Append("{\"format\":\"papertodo.paper-export\",\"schemaVersion\":1,\"papers\":[");
        for (var index = 0; index <= ImportPlanParser.MaxPapers; index++)
        {
            if (index > 0)
            {
                builder.Append(',');
            }

            builder.Append("{}");
        }

        builder.Append("]}");

        var plan = ImportPlanParser.Parse(builder.ToString());

        Assert.NotNull(plan.FatalError);
        Assert.Contains("上限", plan.FatalError);
    }

    [Fact]
    public void Duplicate_source_id_message_is_shortened()
    {
        // 正常长度：仍然按重复来源 ID 报错。
        var duplicated = "0123456789abcdef0123456789abcdef";
        var json =
            "{\"format\":\"papertodo.paper-export\",\"schemaVersion\":1,\"papers\":[" +
            $"{{\"sourceId\":\"{duplicated}\",\"type\":\"note\",\"title\":\"A\",\"content\":\"\"}}," +
            $"{{\"sourceId\":\"{duplicated}\",\"type\":\"note\",\"title\":\"B\",\"content\":\"\"}}" +
            "]}";

        var plan = ImportPlanParser.Parse(json);

        Assert.NotNull(plan.FatalError);
        Assert.Contains("重复的 sourceId", plan.FatalError);
        Assert.True(plan.FatalError!.Length < 200);
    }

    [Fact]
    public void Overlong_source_ids_are_skipped_without_giant_messages()
    {
        // 超过 256 字符的来源 ID 会整张跳过，不应把超长文本拼进提示或查重错误。
        var huge = new string('a', 10_000);
        var json =
            "{\"format\":\"papertodo.paper-export\",\"schemaVersion\":1,\"papers\":[" +
            "{\"sourceId\":\"okid\",\"type\":\"note\",\"title\":\"保留\",\"content\":\"正文\"}," +
            $"{{\"sourceId\":\"{huge}\",\"type\":\"note\",\"title\":\"A\",\"content\":\"\"}}," +
            $"{{\"sourceId\":\"{huge}\",\"type\":\"note\",\"title\":\"B\",\"content\":\"\"}}" +
            "]}";

        var plan = ImportPlanParser.Parse(json);

        Assert.Null(plan.FatalError);
        Assert.NotEmpty(plan.Warnings);
        Assert.All(plan.Warnings, warning => Assert.True(warning.Length < 500));
    }

    [Fact]
    public void Utf16_without_bom_is_rejected_instead_of_importing_nuls()
    {
        // “ab” 的 UTF-16LE 字节（无 BOM）是合法 UTF-8，但会解码出 NUL。
        byte[] bytes = [0x61, 0x00, 0x62, 0x00];

        Assert.Throws<ImportFileEncodingException>(
            () => JsonFiles.DecodeText(bytes, "nobom.md"));
    }
}
