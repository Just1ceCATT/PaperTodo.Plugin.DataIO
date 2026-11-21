using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PaperTodo.Plugin.DataIo;

/// <summary>导出文件的顶层结构。schemaVersion 变化时必须保持向后兼容的读取逻辑。</summary>
internal sealed class ExportDocument
{
    public const string FormatName = "papertodo.paper-export";
    public const int CurrentSchemaVersion = 1;

    public string Format { get; set; } = FormatName;
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public DateTimeOffset ExportedAt { get; set; }
    public ExportSource Source { get; set; } = new();
    public List<ExportPaper> Papers { get; set; } = [];
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
    /// <summary>导出时的宿主纸片 ID，仅用于溯源与同一文件内链接还原。</summary>
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
    /// <summary>文件/文件夹路径链接仅作参考，宿主无写入 API。</summary>
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
        // 保留中文原文，避免 \uXXXX 让导出文件难以阅读。
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}
