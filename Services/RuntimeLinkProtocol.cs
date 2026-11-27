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
    /// <summary>Runtime 确认替换已保存后的回执：Body 收到后才删除待推送恢复日志。</summary>
    public const string ReplaceAckType = "workspace.replace.ack";
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

    public static JsonElement CreateReplace(long requestId, RuntimeWorkspaceState state) =>
        JsonSerializer.SerializeToElement(
            new RuntimeWorkspaceMessage
            {
                Type = ReplaceType,
                RequestId = requestId,
                Workspace = state.Clone()
            },
            Options);

    public static JsonElement CreateReplaceAck(long requestId) =>
        JsonSerializer.SerializeToElement(
            new RuntimeWorkspaceMessage { Type = ReplaceAckType, RequestId = requestId },
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
                if (workspace is not null)
                {
                    // 外部消息中的集合字段可能显式为 null；不归一化会在合并历史时抛异常。
                    workspace.ImportLinks = DataIoState.NormalizeImportLinks(workspace.ImportLinks);
                    workspace.RemovedLinks = DataIoState.NormalizeRemovedLinks(workspace.RemovedLinks);
                    workspace.History = DataIoState.NormalizeHistory(workspace.History);
                    workspace.LastExportDirectory ??= "";
                    workspace.LastImportDirectory ??= "";
                }
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
        RemovedLinks = new Dictionary<string, string>(
            state.RemovedLinks ?? new Dictionary<string, string>(StringComparer.Ordinal),
            StringComparer.Ordinal),
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
            state.RemovedLinks = DataIoState.NormalizeRemovedLinks(state.RemovedLinks);
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
    /// 把 incoming 合并进 target：映射按较新导入时间取胜，墓碑优先于旧映射，
    /// 历史按唯一键去重，上次目录在 incoming 非空时覆盖（纸片镜像通常是最近一次用户操作）。
    /// </summary>
    public static bool MergeState(RuntimeWorkspaceState target, RuntimeWorkspaceState incoming)
    {
        // 删除墓碑必须先合并：合并协议本身只能表达“有”，
        // 不处理墓碑会把本地已删除的映射原样并回来。
        var changed = MergeRemovedLinks(target.RemovedLinks, incoming.RemovedLinks);
        // 已被墓碑覆盖的旧映射直接跳过，避免“先并回、再删掉”这种无意义的抖动。
        var surviving = incoming.ImportLinks
            .Where(pair => !IsRemoved(pair.Value, pair.Key, target.RemovedLinks))
            .ToList();
        changed |= ImportLinkMerge.MergeInto(target.ImportLinks, surviving);
        changed |= ApplyRemovals(target.ImportLinks, target.RemovedLinks);
        changed |= PruneRemovedLinks(target.ImportLinks, target.RemovedLinks);

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
        state.RemovedLinks ??= new Dictionary<string, string>(StringComparer.Ordinal);
        // 先合并墓碑，后续所有映射都按“墓碑优先”过滤。
        var changed = MergeRemovedLinks(state.RemovedLinks, snapshot.RemovedLinks);
        if (replaceLinks)
        {
            var links = DataIoState.NormalizeImportLinks(snapshot.ImportLinks);
            // Runtime 快照可能落后于本地刚发生的删除：本地墓碑优先，不能被快照复活。
            ApplyRemovals(links, state.RemovedLinks);
            if (!LinkSetsEqual(state.ImportLinks, links))
            {
                state.ImportLinks = links;
                changed = true;
            }
        }
        else
        {
            var surviving = snapshot.ImportLinks
                .Where(pair => !IsRemoved(pair.Value, pair.Key, state.RemovedLinks))
                .ToList();
            changed |= ImportLinkMerge.MergeInto(state.ImportLinks, surviving);
            changed |= ApplyRemovals(state.ImportLinks, state.RemovedLinks);
        }

        changed |= PruneRemovedLinks(state.ImportLinks, state.RemovedLinks);

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

    /// <summary>
    /// 应用删除墓碑：只有映射登记时间早于删除时间时才真正删除。
    /// 删除之后重新导入成功的映射（登记时间更新）会保留。
    /// </summary>
    internal static bool ApplyRemovals(
        Dictionary<string, ImportIdentityLink> links,
        IReadOnlyDictionary<string, string>? removed)
    {
        if (removed is null || removed.Count == 0)
        {
            return false;
        }

        var changed = false;
        foreach (var (sourceId, removedAt) in removed)
        {
            if (links.TryGetValue(sourceId, out var link) &&
                ImportIdentityService.CompareImportedAt(link.LastImportedAt, removedAt) < 0)
            {
                links.Remove(sourceId);
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>映射是否已被更晚的删除墓碑覆盖：登记时间早于删除时间才算已删除。</summary>
    private static bool IsRemoved(
        ImportIdentityLink? link,
        string sourceId,
        IReadOnlyDictionary<string, string>? removed) =>
        link is not null &&
        removed is not null &&
        removed.TryGetValue(sourceId, out var removedAt) &&
        ImportIdentityService.CompareImportedAt(link.LastImportedAt, removedAt) < 0;

    /// <summary>合并墓碑集合：同一 sourceId 保留较晚的删除时间。</summary>
    internal static bool MergeRemovedLinks(
        Dictionary<string, string> target,
        IReadOnlyDictionary<string, string>? incoming)
    {
        if (incoming is null || incoming.Count == 0)
        {
            return false;
        }

        var changed = false;
        foreach (var (sourceId, removedAt) in incoming)
        {
            if (string.IsNullOrWhiteSpace(sourceId) || string.IsNullOrWhiteSpace(removedAt))
            {
                continue;
            }

            if (!target.TryGetValue(sourceId, out var existing) ||
                ImportIdentityService.CompareImportedAt(removedAt, existing) > 0)
            {
                target[sourceId] = removedAt;
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>
    /// 丢弃已被更新映射覆盖的墓碑（删除后重新导入），并按保留期与容量上限裁剪，
    /// 避免墓碑集合随使用无限增长。
    /// </summary>
    private static bool PruneRemovedLinks(
        Dictionary<string, ImportIdentityLink> links,
        Dictionary<string, string> removed)
    {
        if (removed.Count == 0)
        {
            return false;
        }

        var changed = false;
        foreach (var sourceId in removed.Keys.ToList())
        {
            if (links.TryGetValue(sourceId, out var link) &&
                ImportIdentityService.CompareImportedAt(link.LastImportedAt, removed[sourceId]) >= 0)
            {
                removed.Remove(sourceId);
                changed = true;
            }
        }

        var normalized = DataIoState.NormalizeRemovedLinks(removed);
        if (normalized.Count != removed.Count ||
            normalized.Any(pair =>
                !removed.TryGetValue(pair.Key, out var existing) ||
                !string.Equals(existing, pair.Value, StringComparison.Ordinal)))
        {
            removed.Clear();
            foreach (var (sourceId, removedAt) in normalized)
            {
                removed[sourceId] = removedAt;
            }

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
                link.LocalFingerprintUnknown != other.LocalFingerprintUnknown ||
                !string.Equals(link.LastImportedAt, other.LastImportedAt, StringComparison.Ordinal) ||
                !(link.PreviousLocalPaperIds ?? []).SequenceEqual(
                    other.PreviousLocalPaperIds ?? [], StringComparer.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 待推送工作区状态日志：Runtime 暂时不可用时落盘，Runtime 启动或恢复后合并回权威状态。
    /// 文件名按工作区隔离（data.json → data，data2.json → data2），
    /// 避免任一工作区保存成功时删除其它工作区尚未推送的数据。
    /// </summary>
    public static string? PendingStatePath(string? workspaceFileName)
    {
        try
        {
            var directory = Path.GetDirectoryName(typeof(DataIoPlugin).Assembly.Location);
            return string.IsNullOrEmpty(directory)
                ? null
                : Path.Combine(
                    directory,
                    ".runtime",
                    $"import-links.pending.{WorkspaceKey(workspaceFileName)}.json");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>把工作区文件名归一化成安全的日志键；无法识别时落到 default。</summary>
    internal static string WorkspaceKey(string? workspaceFileName)
    {
        if (string.IsNullOrWhiteSpace(workspaceFileName))
        {
            return "default";
        }

        var name = Path.GetFileNameWithoutExtension(workspaceFileName);
        var builder = new System.Text.StringBuilder(name.Length);
        foreach (var character in name)
        {
            builder.Append(char.IsLetterOrDigit(character)
                ? char.ToLowerInvariant(character)
                : '_');
        }

        return builder.Length == 0 ? "default" : builder.ToString();
    }

    public static void WritePendingState(RuntimeWorkspaceState state, string? workspaceFileName)
    {
        var path = PendingStatePath(workspaceFileName);
        if (path is null)
        {
            return;
        }

        try
        {
            // 同一工作区可能已有待推送内容：先读取再合并，保留较新的映射与历史。
            var merged = new RuntimeWorkspaceState();
            if (TryReadPendingState(workspaceFileName) is { } existing)
            {
                MergeState(merged, existing);
            }

            MergeState(merged, state);

            // 原子写入 + 落盘：日志本身是崩溃恢复凭据，不能留下半截 JSON。
            JsonFiles.WriteAllTextAtomic(path, SerializeState(merged));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // 日志只是兜底；失败时继续依赖纸片镜像。
        }
    }

    /// <summary>
    /// 状态文件读取上限。正常内容（最多 4096 条映射 + 20 条历史）远小于该值；
    /// 被外部程序替换成数 GB 文件时必须拒绝整读，否则崩溃恢复阶段会耗尽内存。
    /// </summary>
    private const long MaxStateFileBytes = 16L * 1024 * 1024;

    /// <summary>有上限地读取状态文件；不存在或超限时返回 null。</summary>
    private static string? TryReadStateFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxStateFileBytes)
            {
                return null;
            }

            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    public static RuntimeWorkspaceState? TryReadPendingState(string? workspaceFileName)
    {
        var path = PendingStatePath(workspaceFileName);
        if (path is null)
        {
            return null;
        }

        var text = TryReadStateFile(path);
        if (text is null)
        {
            return null;
        }

        try
        {
            var state = ParseState(text, out var corruptBackup);
            if (!string.IsNullOrEmpty(corruptBackup))
            {
                // 待推送日志同样是崩溃恢复凭据：损坏时另存 .bak 后再清除，
                // 不能让它被当成“空日志”静默删除。
                if (WriteCorruptBackup(corruptBackup, workspaceFileName))
                {
                    DeletePendingState(workspaceFileName);
                }

                return null;
            }

            return state;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    public static bool PendingStateOlderThan(string? workspaceFileName, TimeSpan age)
    {
        var path = PendingStatePath(workspaceFileName);
        if (path is null || !File.Exists(path))
        {
            return false;
        }

        try
        {
            return DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > age;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>旧版共享待推送日志路径；升级兼容用，迁移完成后删除。</summary>
    private static string? LegacyPendingStatePath
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

    public static RuntimeWorkspaceState? TryReadLegacyPendingState()
    {
        var path = LegacyPendingStatePath;
        if (path is null)
        {
            return null;
        }

        var text = TryReadStateFile(path);
        if (text is null)
        {
            return null;
        }

        try
        {
            var state = ParseState(text, out var corruptBackup);
            if (!string.IsNullOrEmpty(corruptBackup))
            {
                WriteCorruptBackup(corruptBackup, "legacy");
                return null;
            }

            return state;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// 从旧版共享日志中移除已被本工作区合并的条目，未匹配的（可能属于其它工作区）保留。
    /// 条目清空后删除文件。
    /// </summary>
    public static void RemoveLegacyPendingSources(IReadOnlyCollection<string> consumedSourceIds)
    {
        var path = LegacyPendingStatePath;
        var legacy = TryReadLegacyPendingState();
        if (path is null || legacy is null)
        {
            return;
        }

        try
        {
            var consumed = new HashSet<string>(consumedSourceIds, StringComparer.Ordinal);
            var remaining = legacy.ImportLinks
                .Where(pair => !consumed.Contains(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            if (remaining.Count == 0)
            {
                File.Delete(path);
                return;
            }

            JsonFiles.WriteAllTextAtomic(
                path,
                SerializeState(new RuntimeWorkspaceState { ImportLinks = remaining }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // 迁移失败时保留原文件，下次启动再试。
        }
    }

    /// <summary>
    /// 把损坏状态的原始内容另存为独立备份文件。状态文件随后会以空状态覆盖，
    /// 备份文件保证原始内容不丢，也避免“损坏标记”被永久带入每次保存。
    /// </summary>
    public static bool WriteCorruptBackup(string content, string? workspaceFileName)
    {
        try
        {
            var directory = Path.GetDirectoryName(typeof(DataIoPlugin).Assembly.Location);
            if (string.IsNullOrEmpty(directory))
            {
                return false;
            }

            var path = Path.Combine(
                directory,
                ".runtime",
                $"state-corrupt.{WorkspaceKey(workspaceFileName)}.bak");
            JsonFiles.WriteAllTextAtomic(path, content);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // 备份失败时保留原位内容：调用方不应清空状态。
            return false;
        }
    }

    public static void DeletePendingState(string? workspaceFileName)
    {
        var path = PendingStatePath(workspaceFileName);
        if (path is null)
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
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

    /// <summary>已删除映射的墓碑：sourceId → 删除时间，合并时据此真正删除旧映射。</summary>
    public Dictionary<string, string> RemovedLinks { get; set; } =
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
        RemovedLinks = new Dictionary<string, string>(RemovedLinks, StringComparer.Ordinal),
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
