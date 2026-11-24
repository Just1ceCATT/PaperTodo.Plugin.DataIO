using System.Globalization;
using System.IO;
using System.Text.Json;

namespace PaperTodo.Plugin.DataIo;

/// <summary>
/// Body 与 provider Runtime 之间的工作区状态同步协议。
/// Runtime 状态按工作区隔离、独立于单张纸片生命周期，是导入映射、历史记录和上次目录的权威存储；
/// 每张纸片自己的 state 只在 Runtime 不可用时作为回退镜像。
/// </summary>
internal static class RuntimeLinkProtocol
{
    /// <summary>会话启动时的合并请求：把旧版纸片镜像迁移进 Runtime，并取回合并后的快照。</summary>
    public const string MergeType = "workspace.merge";
    /// <summary>映射/历史变化后的权威替换：Runtime 以整份快照为准（显式删除语义）。</summary>
    public const string ReplaceType = "workspace.replace";
    public const string SnapshotType = "workspace.snapshot";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    public static JsonElement CreateMergeRequest(long requestId, RuntimeWorkspaceState state) =>
        JsonSerializer.SerializeToElement(
            new RuntimeWorkspaceMessage
            {
                Type = MergeType,
                RequestId = requestId,
                Workspace = state.Clone()
            },
            Options);

    public static JsonElement CreateReplace(RuntimeWorkspaceState state) =>
        JsonSerializer.SerializeToElement(
            new RuntimeWorkspaceMessage { Type = ReplaceType, Workspace = state.Clone() },
            Options);

    public static JsonElement CreateSnapshot(
        long requestId,
        RuntimeWorkspaceState state,
        bool corrupt) =>
        JsonSerializer.SerializeToElement(
            new RuntimeWorkspaceMessage
            {
                Type = SnapshotType,
                RequestId = requestId,
                Workspace = state.Clone(),
                Corrupt = corrupt
            },
            Options);

    public static bool TryRead(
        JsonElement message,
        out string type,
        out long requestId,
        out RuntimeWorkspaceState? workspace,
        out bool corrupt)
    {
        type = message.ValueKind == JsonValueKind.Object &&
               TryGetProperty(message, "type", out var typeElement) &&
               typeElement.ValueKind == JsonValueKind.String
            ? typeElement.GetString() ?? ""
            : "";
        requestId = message.ValueKind == JsonValueKind.Object &&
                    TryGetProperty(message, "requestId", out var requestElement) &&
                    requestElement.ValueKind == JsonValueKind.Number &&
                    requestElement.TryGetInt64(out var id)
            ? id
            : 0;
        corrupt = message.ValueKind == JsonValueKind.Object &&
                  TryGetProperty(message, "corrupt", out var corruptElement) &&
                  corruptElement.ValueKind == JsonValueKind.True;
        workspace = null;
        if (type.Length > 0 &&
            TryGetProperty(message, "workspace", out var workspaceElement) &&
            workspaceElement.ValueKind == JsonValueKind.Object)
        {
            try
            {
                workspace = workspaceElement.Deserialize<RuntimeWorkspaceState>(Options);
            }
            catch (JsonException)
            {
                workspace = null;
            }
        }

        return type.Length > 0;
    }

    /// <summary>从纸片状态构造运行时快照。</summary>
    public static RuntimeWorkspaceState FromState(DataIoState state) => new()
    {
        ImportLinks = new Dictionary<string, ImportIdentityLink>(state.ImportLinks, StringComparer.Ordinal),
        History = state.History.Select(entry => entry.Clone()).ToList(),
        LastExportDirectory = state.LastExportDirectory,
        LastImportDirectory = state.LastImportDirectory
    };

    /// <summary>
    /// 读取 Runtime 持久化的工作区状态；损坏时回退为空表，并通过 corruptBackup 保留原始内容。
    /// </summary>
    public static RuntimeWorkspaceState ParseState(string? json, out string? corruptBackup)
    {
        corruptBackup = null;
        var empty = new RuntimeWorkspaceState();
        if (string.IsNullOrWhiteSpace(json))
        {
            return empty;
        }

        try
        {
            var state = JsonSerializer.Deserialize<RuntimeWorkspaceState>(json, Options);
            if (state is null)
            {
                corruptBackup = json;
                return empty;
            }

            state.Version = Math.Max(1, state.Version);
            state.ImportLinks = DataIoState.NormalizeImportLinks(state.ImportLinks);
            state.History = DataIoState.NormalizeHistory(state.History);
            state.LastExportDirectory ??= "";
            state.LastImportDirectory ??= "";
            corruptBackup = state.CorruptBackup;
            return state;
        }
        catch (JsonException)
        {
            corruptBackup = json;
            return empty;
        }
        catch (NotSupportedException)
        {
            corruptBackup = json;
            return empty;
        }
    }

    public static string SerializeState(RuntimeWorkspaceState state) =>
        JsonSerializer.Serialize(state.Clone(), Options);

    /// <summary>
    /// 把 incoming 合并进 target：映射按较新导入时间取胜，历史按唯一键去重，
    /// 上次目录在 incoming 非空时覆盖（纸片镜像通常是最近一次用户操作）。
    /// </summary>
    public static bool MergeState(RuntimeWorkspaceState target, RuntimeWorkspaceState incoming)
    {
        var changed = ImportLinkMerge.MergeInto(target.ImportLinks, incoming.ImportLinks);

        var history = MergeHistory(target.History, incoming.History);
        if (!history.Select(EntryKey).SequenceEqual(target.History.Select(EntryKey)))
        {
            target.History = history;
            changed = true;
        }

        if (!string.IsNullOrEmpty(incoming.LastExportDirectory) &&
            !string.Equals(target.LastExportDirectory, incoming.LastExportDirectory, StringComparison.Ordinal))
        {
            target.LastExportDirectory = incoming.LastExportDirectory;
            changed = true;
        }

        if (!string.IsNullOrEmpty(incoming.LastImportDirectory) &&
            !string.Equals(target.LastImportDirectory, incoming.LastImportDirectory, StringComparison.Ordinal))
        {
            target.LastImportDirectory = incoming.LastImportDirectory;
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// 把快照应用回纸片状态。replaceLinks 为 true 时以 Runtime 的映射为准（删除语义生效），
    /// 否则只做合并，避免纸片列表暂时不可读时丢掉本地镜像。
    /// </summary>
    public static bool ApplyToState(
        DataIoState state,
        RuntimeWorkspaceState snapshot,
        bool replaceLinks)
    {
        var changed = false;
        if (replaceLinks)
        {
            var links = DataIoState.NormalizeImportLinks(snapshot.ImportLinks);
            if (!LinkSetsEqual(state.ImportLinks, links))
            {
                state.ImportLinks = links;
                changed = true;
            }
        }
        else
        {
            changed = ImportLinkMerge.MergeInto(state.ImportLinks, snapshot.ImportLinks);
        }

        var history = MergeHistory(state.History, snapshot.History);
        if (!history.Select(EntryKey).SequenceEqual(state.History.Select(EntryKey)))
        {
            state.History = history;
            changed = true;
        }

        if (!string.IsNullOrEmpty(snapshot.LastExportDirectory) &&
            !string.Equals(state.LastExportDirectory, snapshot.LastExportDirectory, StringComparison.Ordinal))
        {
            state.LastExportDirectory = snapshot.LastExportDirectory;
            changed = true;
        }

        if (!string.IsNullOrEmpty(snapshot.LastImportDirectory) &&
            !string.Equals(state.LastImportDirectory, snapshot.LastImportDirectory, StringComparison.Ordinal))
        {
            state.LastImportDirectory = snapshot.LastImportDirectory;
            changed = true;
        }

        return changed;
    }

    private static bool LinkSetsEqual(
        IReadOnlyDictionary<string, ImportIdentityLink> left,
        IReadOnlyDictionary<string, ImportIdentityLink> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        foreach (var (sourceId, link) in left)
        {
            if (!right.TryGetValue(sourceId, out var other) ||
                !string.Equals(link.LocalPaperId, other.LocalPaperId, StringComparison.Ordinal) ||
                !string.Equals(link.Type, other.Type, StringComparison.Ordinal) ||
                !string.Equals(link.Title, other.Title, StringComparison.Ordinal) ||
                !string.Equals(link.LocalFingerprint, other.LocalFingerprint, StringComparison.Ordinal) ||
                !string.Equals(link.LastImportedAt, other.LastImportedAt, StringComparison.Ordinal) ||
                !(link.PreviousLocalPaperIds ?? []).SequenceEqual(
                    other.PreviousLocalPaperIds ?? [], StringComparer.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>待推送映射日志：Runtime 暂时不可用时落盘，Runtime 启动或恢复后合并回权威状态。</summary>
    public static string? PendingLinksPath
    {
        get
        {
            try
            {
                var directory = Path.GetDirectoryName(typeof(DataIoPlugin).Assembly.Location);
                return string.IsNullOrEmpty(directory)
                    ? null
                    : Path.Combine(directory, ".runtime", "import-links.pending.json");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                return null;
            }
        }
    }

    public static void WritePendingLinks(IReadOnlyDictionary<string, ImportIdentityLink> links)
    {
        var path = PendingLinksPath;
        if (path is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = SerializeState(new RuntimeWorkspaceState
            {
                ImportLinks = new Dictionary<string, ImportIdentityLink>(links, StringComparer.Ordinal)
            });
            var temp = path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 日志只是兜底；失败时继续依赖纸片镜像。
        }
    }

    public static Dictionary<string, ImportIdentityLink>? TryReadPendingLinks()
    {
        var path = PendingLinksPath;
        if (path is null || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var pending = ParseState(File.ReadAllText(path), out _);
            return pending.ImportLinks.Count > 0 ? pending.ImportLinks : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static bool PendingLinksOlderThan(TimeSpan age)
    {
        var path = PendingLinksPath;
        if (path is null || !File.Exists(path))
        {
            return false;
        }

        try
        {
            return DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > age;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static void DeletePendingLinks()
    {
        var path = PendingLinksPath;
        if (path is null)
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 忽略：下次 Runtime 启动会再次合并同一条目（合并是幂等的）。
        }
    }

    /// <summary>只保留当前目标或任一历史副本仍存在的映射。</summary>
    public static Dictionary<string, ImportIdentityLink> FilterKnownLinks(
        IReadOnlyDictionary<string, ImportIdentityLink> links,
        IReadOnlySet<string> knownPaperIds)
    {
        var filtered = new Dictionary<string, ImportIdentityLink>(StringComparer.Ordinal);
        foreach (var (sourceId, link) in links)
        {
            if (knownPaperIds.Contains(link.LocalPaperId) ||
                (link.PreviousLocalPaperIds?.Any(knownPaperIds.Contains) ?? false))
            {
                filtered[sourceId] = link;
            }
        }

        return filtered;
    }

    /// <summary>按（类型、时间、文件）去重后排序并截断，避免迁移时重复插入历史。</summary>
    private static List<DataIoHistoryEntry> MergeHistory(
        IEnumerable<DataIoHistoryEntry> left,
        IEnumerable<DataIoHistoryEntry> right)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var combined = new List<DataIoHistoryEntry>();
        foreach (var entry in right.Concat(left))
        {
            if (entry is null || !seen.Add(EntryKey(entry)))
            {
                continue;
            }

            combined.Add(entry.Clone());
        }

        return DataIoState.NormalizeHistory(combined);
    }

    private static string EntryKey(DataIoHistoryEntry entry) =>
        entry.Kind + "" + entry.At + "" + entry.FilePath;

    /// <summary>属性名按不区分大小写读取，兼容被其他工具改写为 PascalCase 的文件。</summary>
    internal static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private sealed class RuntimeWorkspaceMessage
    {
        public string Type { get; set; } = "";
        public long RequestId { get; set; }
        public RuntimeWorkspaceState? Workspace { get; set; }
        public bool Corrupt { get; set; }
    }
}

/// <summary>Runtime 与 Body 共用的工作区级状态文档。</summary>
internal sealed class RuntimeWorkspaceState
{
    public int Version { get; set; } = DataIoState.CurrentVersion;

    public Dictionary<string, ImportIdentityLink> ImportLinks { get; set; } =
        new(StringComparer.Ordinal);

    public List<DataIoHistoryEntry> History { get; set; } = [];

    public string LastExportDirectory { get; set; } = "";

    public string LastImportDirectory { get; set; } = "";

    /// <summary>损坏状态文件的原始内容；读取失败时保留，保存时会写回，避免静默丢数据。</summary>
    public string? CorruptBackup { get; set; }

    public RuntimeWorkspaceState Clone() => new()
    {
        Version = Version,
        ImportLinks = new Dictionary<string, ImportIdentityLink>(ImportLinks, StringComparer.Ordinal),
        History = History.Select(entry => entry.Clone()).ToList(),
        LastExportDirectory = LastExportDirectory,
        LastImportDirectory = LastImportDirectory,
        CorruptBackup = CorruptBackup
    };
}

/// <summary>Runtime 与 Body 共用的映射合并规则。</summary>
internal static class ImportLinkMerge
{
    /// <summary>把 incoming 合并进 target：同一 sourceId 以较新的导入时间为准。</summary>
    public static bool MergeInto(
        Dictionary<string, ImportIdentityLink> target,
        IEnumerable<KeyValuePair<string, ImportIdentityLink>>? incoming)
    {
        if (incoming is null)
        {
            return false;
        }

        var changed = false;
        foreach (var (sourceId, link) in incoming)
        {
            if (string.IsNullOrWhiteSpace(sourceId) || link is null)
            {
                continue;
            }

            if (!target.TryGetValue(sourceId, out var existing) ||
                ImportIdentityService.CompareImportedAt(link.LastImportedAt, existing.LastImportedAt) > 0)
            {
                target[sourceId] = link;
                changed = true;
            }
        }

        return ImportIdentityService.Trim(target) || changed;
    }
}
