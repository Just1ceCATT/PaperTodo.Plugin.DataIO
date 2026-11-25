using System.Text.Json;
using PaperTodo.Plugin;

namespace PaperTodo.Plugin.DataIo;

/// <summary>
/// provider 级后台 Runtime：持久化导入身份映射、历史记录和上次目录。
/// Runtime state 按工作区隔离、不随单张 DataIO 纸片删除，
/// 因此删除或重建工具纸片后这些数据仍然存在。
/// </summary>
internal sealed class DataIoRuntime : IPaperPluginRuntime
{
    private readonly PaperPluginRuntimeContext _context;
    private readonly object _gate = new();
    private readonly RuntimeWorkspaceState _state;
    private readonly IDisposable _subscription;

    public DataIoRuntime(PaperPluginRuntimeContext context)
    {
        _context = context;
        _state = RuntimeLinkProtocol.ParseState(context.State.Json, out var corruptBackup);
        if (!string.IsNullOrEmpty(corruptBackup))
        {
            _state.CorruptBackup = corruptBackup;
        }

        // Runtime 不可用期间落盘的待推送映射：先按当前工作区纸片校验，再合并并删除日志。
        var pending = RuntimeLinkProtocol.TryReadPendingLinks();
        if (pending is not null)
        {
            try
            {
                var known = context.Workspace.ListPapers()
                    .Select(paper => paper.Id)
                    .ToHashSet(StringComparer.Ordinal);
                var filtered = RuntimeLinkProtocol.FilterKnownLinks(pending, known);
                if (ImportLinkMerge.MergeInto(_state.ImportLinks, filtered))
                {
                    Save();
                }

                // 日志里没有任何属于本工作区的目标时，可能是其它工作区的待推送数据，先保留。
                if (filtered.Count > 0 ||
                    pending.Count == 0 ||
                    RuntimeLinkProtocol.PendingLinksOlderThan(TimeSpan.FromDays(30)))
                {
                    RuntimeLinkProtocol.DeletePendingLinks();
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // 纸片列表暂时不可读：保留日志，等下次 Runtime 启动再合并。
            }
        }

        _subscription = context.Papers.Subscribe(OnRuntimeEvent);

        // 宿主以折叠/胶囊状态恢复工具纸片时会延迟构建正文外壳，展开后可能只留下空白窗口。
        // Runtime 比 Body 更早稳定，启动后主动请求一次“展开”，强制宿主走正常的正文构建路径。
        _ = Task.Run(() => NormalizeStartupPresentationAsync(context));
    }

    private static async Task NormalizeStartupPresentationAsync(PaperPluginRuntimeContext context)
    {
        try
        {
            // 等宿主完成启动纸片恢复与首帧展示。
            await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            if (!AutoStartEnabled(context.Settings.Json) ||
                context.Workspace is not IPaperWorkspacePresentationApi presentation)
            {
                return;
            }

            foreach (var paper in context.Workspace.ListPapers())
            {
                if (!paper.IsVisible ||
                    !string.Equals(paper.Type, "note", StringComparison.Ordinal) ||
                    !string.Equals(
                        paper.BodyProviderId,
                        context.ProviderId,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                try
                {
                    presentation.ExpandPaper(paper.Id, activate: false);
                }
                catch (PaperTodoPluginException)
                {
                    // 单个纸片不可展示时继续处理其它纸片。
                }
            }
        }
        catch (Exception)
        {
            // 启动补建只是兜底；宿主关闭或权限变化时静默放弃。
        }
    }

    /// <summary>读取插件设置中的“启动后自动打开”；缺失时按 plugin.json 的默认值 true 处理。</summary>
    private static bool AutoStartEnabled(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson))
        {
            return true;
        }

        try
        {
            using var document = JsonDocument.Parse(settingsJson);
            return !document.RootElement.TryGetProperty("autoStart", out var value) ||
                   value.ValueKind != JsonValueKind.False;
        }
        catch (JsonException)
        {
            return true;
        }
    }

    private void OnRuntimeEvent(PaperPluginRuntimeEvent evt)
    {
        if (evt.Kind != PaperPluginRuntimeEventKind.Message ||
            evt.Message is not { } message)
        {
            return;
        }

        try
        {
            HandleMessage(evt.PaperId, message);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 单条同步消息失败不能影响 Runtime 或其它纸片；Body 侧会按超时回退到镜像。
        }
    }

    private void HandleMessage(string paperId, JsonElement message)
    {
        if (!RuntimeLinkProtocol.TryRead(
                message,
                out var type,
                out var requestId,
                out var workspace,
                out _) ||
            workspace is null)
        {
            return;
        }

        switch (type)
        {
            case RuntimeLinkProtocol.MergeType:
                lock (_gate)
                {
                    if (RuntimeLinkProtocol.MergeState(_state, workspace))
                    {
                        SaveLocked();
                    }
                }

                ReplySnapshot(paperId, requestId);
                break;

            case RuntimeLinkProtocol.ReplaceType:
                lock (_gate)
                {
                    // 显式替换：Body 在成功同步后提交完整快照，删除语义必须真正生效。
                    _state.ImportLinks = DataIoState.NormalizeImportLinks(workspace.ImportLinks);
                    _state.History = DataIoState.NormalizeHistory(workspace.History);
                    _state.LastExportDirectory = workspace.LastExportDirectory ?? "";
                    _state.LastImportDirectory = workspace.LastImportDirectory ?? "";
                    SaveLocked();
                }

                break;
        }
    }

    private void ReplySnapshot(string paperId, long requestId)
    {
        RuntimeWorkspaceState snapshot;
        bool corrupt;
        lock (_gate)
        {
            snapshot = _state.Clone();
            // 原始损坏内容只留在持久化文件里，不随每次快照发给 Body。
            snapshot.CorruptBackup = null;
            corrupt = !string.IsNullOrEmpty(_state.CorruptBackup);
        }

        _context.Papers.PostToBody(
            paperId,
            RuntimeLinkProtocol.CreateSnapshot(requestId, snapshot, corrupt));
    }

    private void Save()
    {
        lock (_gate)
        {
            SaveLocked();
        }
    }

    private void SaveLocked() =>
        _context.State.Save(RuntimeLinkProtocol.SerializeState(_state));

    public void Dispose() => _subscription.Dispose();
}
