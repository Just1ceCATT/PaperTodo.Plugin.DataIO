using System.Text.Json;

namespace PaperTodo.Plugin.DataIo;

/// <summary>
/// 每个工具纸片的私有状态，只保存轻量 UI 偏好，不保存勾选状态或任何文件内容。
/// 由宿主通过 PaperBodyContext.StateJson / SaveStateJson 托管持久化。
/// </summary>
internal sealed class DataIoState
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public string LastExportDirectory { get; set; } = "";
    public string LastImportDirectory { get; set; } = "";
    public string ExportFormat { get; set; } = "json";

    /// <summary>列表搜索关键字；只保留字符串，过滤在会话内完成。</summary>
    public string Search { get; set; } = "";

    /// <summary>
    /// 是否已完成首次自动命名。为 false 时（例如把一篇 Markdown 笔记转换为本插件）
    /// 会在会话创建时把纸片标题设为“DataIO”；之后不再覆盖用户手动改过的标题。
    /// </summary>
    public bool TitleInitialized { get; set; }

    /// <summary>
    /// 导入身份映射：导出文件中的 sourceId → 本工作区纸片 ID。
    /// 首次导入时登记，后续导入同一来源时用于更新原纸片。
    /// </summary>
    public Dictionary<string, ImportIdentityLink> ImportLinks { get; set; } =
        new(StringComparer.Ordinal);

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    /// <summary>容错解析；损坏的旧状态回退为默认值，绝不阻止会话创建。</summary>
    public static DataIoState Parse(string? json) => Parse(json, out _);

    /// <summary>
    /// 容错解析。recovered 为 true 表示状态 JSON 损坏、已回退默认值，
    /// 调用方应备份原始状态并提示用户（导入身份映射可能丢失）。
    /// </summary>
    public static DataIoState Parse(string? json, out bool recovered)
    {
        recovered = false;
        if (string.IsNullOrWhiteSpace(json))
        {
            return new DataIoState();
        }

        try
        {
            var state = JsonSerializer.Deserialize<DataIoState>(json, Options) ?? new DataIoState();
            state.ImportLinks = NormalizeImportLinks(state.ImportLinks);
            return state;
        }
        catch (JsonException)
        {
            recovered = true;
            return new DataIoState();
        }
        catch (NotSupportedException)
        {
            recovered = true;
            return new DataIoState();
        }
    }

    /// <summary>宿主在 stateVersion 升级时调用；当前只有 v1，主要做字段归一化。</summary>
    public static string Migrate(string? stateJson, int fromVersion)
    {
        var state = Parse(stateJson);
        state.Version = CurrentVersion;
        state.LastExportDirectory ??= "";
        state.LastImportDirectory ??= "";
        state.ExportFormat = NormalizeExportFormat(state.ExportFormat);
        state.Search ??= "";
        state.ImportLinks = NormalizeImportLinks(state.ImportLinks);
        return Serialize(state);
    }

    public static string Serialize(DataIoState state) =>
        JsonSerializer.Serialize(state, Options);

    public static string NormalizeExportFormat(string? value) =>
        value is "markdown" ? "markdown" : "json";

    private const int MaxImportLinks = 4096;

    public static Dictionary<string, ImportIdentityLink> NormalizeImportLinks(
        Dictionary<string, ImportIdentityLink>? links)
    {
        var normalized = new Dictionary<string, ImportIdentityLink>(StringComparer.Ordinal);
        if (links == null)
        {
            return normalized;
        }

        foreach (var (sourceId, link) in links)
        {
            if (normalized.Count >= MaxImportLinks)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(sourceId) ||
                link == null ||
                string.IsNullOrWhiteSpace(link.LocalPaperId) ||
                link.Type is not ("todo" or "note"))
            {
                continue;
            }

            link.LocalFingerprint ??= "";
            link.Title ??= "";
            link.LastImportedAt ??= "";
            normalized[sourceId] = link;
        }

        return normalized;
    }
}

/// <summary>一条导入身份映射，附带用于识别本地修改的指纹。</summary>
internal sealed class ImportIdentityLink
{
    public string LocalPaperId { get; set; } = "";
    public string Type { get; set; } = "";
    public string Title { get; set; } = "";

    /// <summary>上次导入完成后，本地纸片内容的指纹；用于检测用户之后的本地修改。</summary>
    public string LocalFingerprint { get; set; } = "";
    public string LastImportedAt { get; set; } = "";
}
