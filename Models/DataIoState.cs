using System.Globalization;
using System.Text.Json;

namespace PaperTodo.Plugin.DataIo;

/// <summary>
/// 每个工具纸片的私有状态，只保存轻量 UI 偏好，不保存勾选状态或任何文件内容。
/// 由 PaperTodo 通过 PaperBodyContext.StateJson / SaveStateJson 托管持久化。
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

    /// <summary>
    /// 已删除映射的墓碑：sourceId → 删除时间（ISO 8601）。
    /// Runtime 的合并协议只能表达“有哪些映射”，无法表达“删掉了哪些”；
    /// 没有墓碑时，本地删除会被权威快照原样回滚，死映射永远删不掉。
    /// </summary>
    public Dictionary<string, string> RemovedLinks { get; set; } =
        new(StringComparer.Ordinal);

    /// <summary>
    /// 最近的导入/导出记录（新到旧），供贴边胶囊的悬停面板展示。
    /// 只保留轻量元数据，条数有上限，避免状态无限增长。
    /// </summary>
    public List<DataIoHistoryEntry> History { get; set; } = [];

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
            state.RemovedLinks = NormalizeRemovedLinks(state.RemovedLinks);
            state.History = NormalizeHistory(state.History);
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

    /// <summary>PaperTodo 在 stateVersion 升级时调用；当前只有 v1，主要做字段归一化。</summary>
    public static string Migrate(string? stateJson, int fromVersion)
    {
        var state = Parse(stateJson, out var recovered);
        if (recovered)
        {
            // 原样返回损坏内容：会话创建时才会备份原始状态并提示用户。
            // 先回退成默认值再序列化会静默丢掉全部导入映射，且没有备份可恢复。
            return stateJson ?? "";
        }

        state.Version = CurrentVersion;
        state.LastExportDirectory ??= "";
        state.LastImportDirectory ??= "";
        state.ExportFormat = NormalizeExportFormat(state.ExportFormat);
        state.Search ??= "";
        state.ImportLinks = NormalizeImportLinks(state.ImportLinks);
        state.RemovedLinks = NormalizeRemovedLinks(state.RemovedLinks);
        state.History = NormalizeHistory(state.History);
        return Serialize(state);
    }

    public static string Serialize(DataIoState state) =>
        JsonSerializer.Serialize(state, Options);

    public static string NormalizeExportFormat(string? value) =>
        value is "markdown" ? "markdown" : "json";

    /// <summary>历史记录条数上限；超出时保留最新的记录。</summary>
    internal const int MaxHistory = 20;

    /// <summary>
    /// 归一化历史记录：丢弃类型或时间无效的条目、修正计数、按时间从新到旧排序并截断。
    /// 损坏的单条记录不应影响其他记录，因此这里逐条跳过而不是整体回退。
    /// </summary>
    public static List<DataIoHistoryEntry> NormalizeHistory(List<DataIoHistoryEntry>? history)
    {
        var normalized = new List<(DataIoHistoryEntry Entry, DateTimeOffset At)>();
        if (history == null)
        {
            return [];
        }

        foreach (var entry in history)
        {
            if (entry == null ||
                entry.Kind is not (DataIoHistoryKinds.Export or DataIoHistoryKinds.Import) ||
                !DateTimeOffset.TryParse(
                    entry.At,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var at))
            {
                continue;
            }

            entry.FilePath ??= "";
            entry.Format = entry.Format is "json" or "markdown" ? entry.Format : "";
            entry.Papers = Math.Max(0, entry.Papers);
            entry.Created = Math.Max(0, entry.Created);
            entry.Updated = Math.Max(0, entry.Updated);
            entry.Skipped = Math.Max(0, entry.Skipped);
            entry.Failed = Math.Max(0, entry.Failed);
            entry.Warnings = Math.Max(0, entry.Warnings);
            normalized.Add((entry, at));
        }

        normalized.Sort((left, right) => right.At.CompareTo(left.At));
        return normalized
            .Take(MaxHistory)
            .Select(pair => pair.Entry)
            .ToList();
    }

    /// <summary>映射条目上限；运行期登记时按最近导入时间淘汰最旧条目。</summary>
    internal const int MaxImportLinks = 4096;

    /// <summary>每个 sourceId 保留的历史本地纸片数量上限（用于当前目标被删除后的回退）。</summary>
    internal const int MaxImportLinkHistory = 8;

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
            link.PreviousLocalPaperIds = (link.PreviousLocalPaperIds ?? [])
                .Where(id => !string.IsNullOrWhiteSpace(id) &&
                             !string.Equals(id, link.LocalPaperId, StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .Take(MaxImportLinkHistory)
                .ToList();
            normalized[sourceId] = link;
        }

        // 超过上限时按最近导入时间淘汰最旧条目，与运行期 Trim 的规则一致。
        // 不能按插入顺序截断：字典顺序是“首次登记顺序”，会把仍在频繁使用的旧来源先扔掉。
        if (normalized.Count > MaxImportLinks)
        {
            var keep = normalized
                .OrderByDescending(pair => ParseImportedAt(pair.Value.LastImportedAt))
                .Take(MaxImportLinks)
                .Select(pair => pair.Key)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var sourceId in normalized.Keys.Where(id => !keep.Contains(id)).ToList())
            {
                normalized.Remove(sourceId);
            }
        }

        return normalized;
    }

    /// <summary>墓碑保留时长；超过后不再保留，避免状态文件随使用无限增长。</summary>
    internal static readonly TimeSpan RemovedLinkRetention = TimeSpan.FromDays(30);

    /// <summary>
    /// 归一化删除墓碑：丢弃无键/无有效时间的条目，按保留期淘汰过期墓碑，
    /// 并按删除时间截断到与映射相同的容量上限。
    /// </summary>
    public static Dictionary<string, string> NormalizeRemovedLinks(
        Dictionary<string, string>? removed)
    {
        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        if (removed == null || removed.Count == 0)
        {
            return normalized;
        }

        var cutoff = DateTimeOffset.Now - RemovedLinkRetention;
        foreach (var (sourceId, at) in removed)
        {
            if (string.IsNullOrWhiteSpace(sourceId) ||
                !DateTimeOffset.TryParse(
                    at,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var parsed) ||
                parsed < cutoff)
            {
                continue;
            }

            normalized[sourceId] = parsed.ToString("O", CultureInfo.InvariantCulture);
        }

        if (normalized.Count > MaxImportLinks)
        {
            var keep = normalized
                .OrderByDescending(pair => ParseImportedAt(pair.Value))
                .Take(MaxImportLinks)
                .Select(pair => pair.Key)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var sourceId in normalized.Keys.Where(id => !keep.Contains(id)).ToList())
            {
                normalized.Remove(sourceId);
            }
        }

        return normalized;
    }

    private static DateTimeOffset ParseImportedAt(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;
}

/// <summary>一条导入身份映射，附带用于识别本地修改的指纹。</summary>
internal sealed class ImportIdentityLink
{
    public string LocalPaperId { get; set; } = "";
    public string Type { get; set; } = "";
    public string Title { get; set; } = "";

    /// <summary>上次导入完成后，本地纸片内容的指纹；用于检测用户之后的本地修改。</summary>
    public string LocalFingerprint { get; set; } = "";

    /// <summary>
    /// 指纹不可信：当前目标是从历史副本回退来的，没有该副本的导入时指纹。
    /// 此时不能把空指纹当作“未修改”，更新前的差异预览必须按“可能已修改”提示。
    /// </summary>
    public bool LocalFingerprintUnknown { get; set; }

    public string LastImportedAt { get; set; } = "";

    /// <summary>
    /// 同一 sourceId 之前指向过的本地纸片 ID（最近优先）。
    /// 选择“新增”后映射会改指向新副本；若新副本被删除，可回退到这些历史副本继续显示差异。
    /// </summary>
    public List<string> PreviousLocalPaperIds { get; set; } = [];
}
