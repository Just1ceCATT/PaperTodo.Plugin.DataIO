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
    public string Filter { get; set; } = "all";
    public string Search { get; set; } = "";

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
            return JsonSerializer.Deserialize<DataIoState>(json, Options) ?? new DataIoState();
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
        state.Search ??= "";
        return Serialize(state);
    }

    public static string Serialize(DataIoState state) =>
        JsonSerializer.Serialize(state, Options);

    public static string NormalizeFilter(string? value) =>
        value is "todo" or "note" ? value : "all";
}
