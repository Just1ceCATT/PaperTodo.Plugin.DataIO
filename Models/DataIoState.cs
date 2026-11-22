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
    public string Filter { get; set; } = "all";
    public string Search { get; set; } = "";

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
    public static DataIoState Parse(string? json)
    {
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
            return new DataIoState();
        }
    }

    /// <summary>宿主在 stateVersion 升级时调用；当前只有 v1，主要做字段归一化。</summary>
    public static string Migrate(string? stateJson, int fromVersion)
    {
        var state = Parse(stateJson);
        state.Version = CurrentVersion;
        state.Filter = NormalizeFilter(state.Filter);
        state.LastExportDirectory ??= "";
        state.LastImportDirectory ??= "";
        state.ExportFormat = NormalizeExportFormat(state.ExportFormat);
        state.Search ??= "";
        state.ImportLinks = NormalizeImportLinks(state.ImportLinks);
        return Serialize(state);
    }

    public static string Serialize(DataIoState state) =>
        JsonSerializer.Serialize(state, Options);

    public static string NormalizeFilter(string? value) =>
        value is "todo" or "note" ? value : "all";

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
