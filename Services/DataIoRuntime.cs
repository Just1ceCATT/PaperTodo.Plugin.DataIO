using System.Text.Json;
using System.Windows;
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
    private volatile bool _disposed;

    /// <summary>本次进程启动时发现过损坏状态；只用于向 Body 提示一次，不写入持久化状态。</summary>
    private readonly bool _startupCorrupt;

    public DataIoRuntime(PaperPluginRuntimeContext context)
    {
        _context = context;
        _state = RuntimeLinkProtocol.ParseState(context.State.Json, out var corruptBackup);
        if (!string.IsNullOrEmpty(corruptBackup))
        {
            _state.CorruptBackup = corruptBackup;
        }

        // Runtime 不可用期间落盘的待推送状态：日志按工作区隔离，
        // 先按当前工作区纸片校验映射，再连同历史与上次目录一起合并并删除日志。
        var workspaceFileName = TryGetWorkspaceFileName(context);

        // 状态文件损坏过一次就永久带“损坏标记”，会让每次启动都重复告警。
        // 原始内容另存为独立备份后清空标记；备份写失败时保持原状，宁可重复告警也不丢数据。
        _startupCorrupt = !string.IsNullOrEmpty(_state.CorruptBackup);
        if (_startupCorrupt &&
            RuntimeLinkProtocol.WriteCorruptBackup(_state.CorruptBackup!, workspaceFileName))
        {
            try
            {
                lock (_gate)
                {
                    _state.CorruptBackup = null;
                    SaveLocked();
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // 保存失败不能让 Runtime 构造失败；恢复内存中的损坏标记，下次启动再备份。
                lock (_gate)
                {
                    _state.CorruptBackup = corruptBackup;
                }
            }
        }

        MergePendingState(context, workspaceFileName);

        _subscription = context.Papers.Subscribe(OnRuntimeEvent);

        // PaperTodo 以折叠/胶囊状态恢复工具纸片时会延迟构建正文外壳，展开后可能只留下空白窗口。
        // Runtime 比 Body 更早稳定，启动后主动请求一次“展开”，强制 PaperTodo 走正常的正文构建路径。
        _ = Task.Run(() => NormalizeStartupPresentationAsync());
    }

    /// <summary>
    /// 合并本工作区的待推送状态。只有按当前纸片列表校验过、或日志确实为空、
    /// 或日志已过期时才删除：列表在启动早期可能不完整，此时删掉会丢映射。
    /// </summary>
    private void MergePendingState(PaperPluginRuntimeContext context, string? workspaceFileName)
    {
        var pending = RuntimeLinkProtocol.TryReadPendingState(workspaceFileName);
        if (pending is not null)
        {
            try
            {
                var known = context.Workspace.ListPapers()
                    .Select(paper => paper.Id)
                    .ToHashSet(StringComparer.Ordinal);
                var filtered = RuntimeLinkProtocol.FilterKnownLinks(pending.ImportLinks, known);
                var incoming = new RuntimeWorkspaceState
                {
                    ImportLinks = filtered,
                    // 墓碑必须一起合并：日志里的删除语义同样要真正生效。
                    RemovedLinks = pending.RemovedLinks,
                    History = pending.History,
                    LastExportDirectory = pending.LastExportDirectory,
                    LastImportDirectory = pending.LastImportDirectory
                };
                if (RuntimeLinkProtocol.MergeState(_state, incoming))
                {
                    Save();
                }

                // 只有日志里的每条映射都能在当前纸片列表中找到，才能确认日志已被完整消费。
                // 列表在启动早期可能不完整：只要有一条被过滤掉（目标暂时不可见），
                // 删除日志就会永久丢掉这条映射，因此保留到列表完整或日志过期。
                if (filtered.Count == pending.ImportLinks.Count ||
                    RuntimeLinkProtocol.PendingStateOlderThan(workspaceFileName, TimeSpan.FromDays(30)))
                {
                    RuntimeLinkProtocol.DeletePendingState(workspaceFileName);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // 纸片列表暂时不可读：保留日志，等下次 Runtime 启动再合并。
            }
        }

        // 升级兼容：旧版共享日志没有工作区后缀，按当前纸片过滤后合并；
        // 未匹配的条目可能属于其它工作区，保留在旧文件中等待对应工作区合并。
        var legacy = RuntimeLinkProtocol.TryReadLegacyPendingState();
        if (legacy is null)
        {
            return;
        }

        try
        {
            var known = context.Workspace.ListPapers()
                .Select(paper => paper.Id)
                .ToHashSet(StringComparer.Ordinal);
            var filtered = RuntimeLinkProtocol.FilterKnownLinks(legacy.ImportLinks, known);
            if (ImportLinkMerge.MergeInto(_state.ImportLinks, filtered))
            {
                Save();
            }

            if (filtered.Count > 0)
            {
                RuntimeLinkProtocol.RemoveLegacyPendingSources(filtered.Keys.ToList());
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 暂时读不到纸片列表：保留旧日志，下次启动再迁移。
        }
    }

    /// <summary>当前工作区数据文件名（data.json / data2.json），用于隔离待推送日志。</summary>
    private static string? TryGetWorkspaceFileName(PaperPluginRuntimeContext context)
    {
        try
        {
            var status = context.Workspace.GetDataReloadStatus();
            return string.IsNullOrWhiteSpace(status.DataFileName) ? null : status.DataFileName;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task NormalizeStartupPresentationAsync()
    {
        try
        {
            // 等 PaperTodo 完成启动纸片恢复与首帧展示。
            await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            if (_disposed)
            {
                return;
            }

            // PaperTodo API 不自行切线程，必须在 UI 线程调用；
            // Runtime 没有 Body 上下文里的 Dispatcher，使用应用主 Dispatcher。
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null)
            {
                return;
            }

            await dispatcher.InvokeAsync(NormalizeStartupPresentation);
        }
        catch (Exception)
        {
            // 启动补建只是兜底；PaperTodo 关闭或权限变化时静默放弃。
        }
    }

    private void NormalizeStartupPresentation()
    {
        if (_disposed || _context.Workspace is not IPaperWorkspacePresentationApi presentation)
        {
            return;
        }

        foreach (var paper in _context.Workspace.ListPapers())
        {
            if (_disposed ||
                !paper.IsVisible ||
                // 用户主动折叠成胶囊的纸片不展开：启动补建只修复“本应展开”的空白外壳，
                // 不能覆盖 PaperTodo 持久化的折叠状态。
                paper.IsCollapsed ||
                !string.Equals(paper.Type, "note", StringComparison.Ordinal) ||
                !string.Equals(
                    paper.BodyProviderId,
                    _context.ProviderId,
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
                    _state.RemovedLinks = DataIoState.NormalizeRemovedLinks(workspace.RemovedLinks);
                    // 快照里的墓碑同样要应用到映射：发送方可能只更新墓碑而漏掉整表。
                    RuntimeLinkProtocol.ApplyRemovals(_state.ImportLinks, _state.RemovedLinks);
                    _state.History = DataIoState.NormalizeHistory(workspace.History);
                    _state.LastExportDirectory = workspace.LastExportDirectory ?? "";
                    _state.LastImportDirectory = workspace.LastImportDirectory ?? "";
                    SaveLocked();
                }

                // 只有保存成功才回执。保存抛异常时不会走到这里，
                // Body 会保留待推送日志，下次启动继续合并，避免“投递成功即删除”丢失映射。
                ReplyReplaceAck(paperId, requestId);
                break;
        }
    }

    private void ReplyReplaceAck(string paperId, long requestId)
    {
        if (requestId <= 0)
        {
            return;
        }

        _context.Papers.PostToBody(paperId, RuntimeLinkProtocol.CreateReplaceAck(requestId));
    }

    private void ReplySnapshot(string paperId, long requestId)
    {
        RuntimeWorkspaceState snapshot;
        bool corrupt;
        lock (_gate)
        {
            snapshot = _state.Clone();
            // 原始损坏内容只留在备份文件里，不随快照发给 Body；
            // 仅在本次进程首次发现损坏时提示，清空标记后不再重复。
            snapshot.CorruptBackup = null;
            corrupt = _startupCorrupt;
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

    public void Dispose()
    {
        _disposed = true;
        _subscription.Dispose();
    }
}
