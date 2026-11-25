using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PaperTodo.Plugin.DataIo;

/// <summary>导出文件的顶层结构。schemaVersion 变化时必须保持向后兼容的读取逻辑。</summary>
internal sealed class ExportDocument
{
    public const string FormatName = "papertodo.paper-export";

    /// <summary>仍可读取的最低结构版本；v1 是不含图片清单的旧格式。</summary>
    public const int MinimumSupportedSchemaVersion = 1;

    /// <summary>当前写入版本；带图片清单的压缩包使用 v2。</summary>
    public const int CurrentSchemaVersion = 2;

    public string Format { get; set; } = FormatName;

    /// <summary>实际写出的结构版本：没有图片时仍写 v1，保持旧插件可读。</summary>
    public int SchemaVersion { get; set; } = MinimumSupportedSchemaVersion;
    public DateTimeOffset ExportedAt { get; set; }
    public ExportSource Source { get; set; } = new();
    public List<ExportPaper> Papers { get; set; } = [];

    /// <summary>压缩包内的图片清单；普通单文件导出为 null。</summary>
    public List<ExportImage>? Images { get; set; }
}

/// <summary>
/// 压缩包中的一张笔记图片。图片文件本身与正文分离存放，
/// 导入时用 ImageId 把正文里的旧引用映射到新写入的图片。
/// </summary>
internal sealed class ExportImage
{
    /// <summary>导出时纸片的 ID，用于把清单和正文对应起来。</summary>
    public string PaperSourceId { get; set; } = "";

    /// <summary>导出机器上的图片 ID，只在源笔记内有效。</summary>
    public string ImageId { get; set; } = "";

    /// <summary>压缩包内的相对路径，统一使用正斜杠。</summary>
    public string File { get; set; } = "";

    public string Mime { get; set; } = "";
    public int ByteLength { get; set; }

    /// <summary>编码后字节的 SHA-256，用于导入前校验文件完整性。</summary>
    public string Sha256 { get; set; } = "";

    /// <summary>
    /// false 表示图片超过插件直接追加的单图上限（8 MiB）。
    /// 导入时会先按宿主规则压缩再写回；GIF/TIFF 等无法安全压缩的格式仍只保留在压缩包中。
    /// </summary>
    public bool Restorable { get; set; } = true;
}

/// <summary>Markdown 压缩包中的独立图片清单；正文里使用相对链接，清单负责还原映射。</summary>
internal sealed class ExportImageManifest
{
    public const string FormatName = "papertodo.dataio.images";

    public string Format { get; set; } = FormatName;
    public int SchemaVersion { get; set; } = 1;
    public List<ExportImage> Images { get; set; } = [];
}

internal sealed class ExportSource
{
    public string PluginId { get; set; } = "";
    public string PluginVersion { get; set; } = "";
    public string ApiVersion { get; set; } = "";
    public string UiLanguage { get; set; } = "";
    public string Note { get; set; } = "";
}

internal sealed class ExportPaper
{
    /// <summary>导出时的宿主纸片 ID，仅用于溯源与同一文件内关联纸片还原。</summary>
    public string SourceId { get; set; } = "";
    public string Type { get; set; } = "todo";
    public string Title { get; set; } = "";
    public bool Show { get; set; } = true;
    public List<ExportTodo>? Todos { get; set; }
    public bool? ContentAvailable { get; set; }
    public string? Content { get; set; }
}

internal sealed class ExportTodo
{
    public string Text { get; set; } = "";
    public bool Done { get; set; }
    public int Order { get; set; }
    public DateTimeOffset? ReminderAt { get; set; }
    public string? LinkedSourcePaperId { get; set; }
    /// <summary>待办关联的文件/文件夹仅作参考，宿主无写入 API。</summary>
    public string? LinkedPath { get; set; }
    public bool? LinkedPathIsDirectory { get; set; }
}

/// <summary>导出与导入共用的 JSON 选项。</summary>
internal static class ExportJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 64,
        // 与宿主读取 data.json 的容错保持一致：允许注释和尾随逗号。
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        // 保留中文原文，避免 \uXXXX 让导出文件难以阅读。
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}
