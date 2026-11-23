using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using PaperTodo.Plugin;

namespace PaperTodo.Plugin.DataIo;

/// <summary>
/// 导入身份适配层：维护“导出 sourceId → 本地纸片 ID”的映射。
/// 首次导入时登记，后续导入同一来源时用于更新原纸片；映射只作为候选目标，使用前必须校验。
/// </summary>
internal sealed class ImportIdentityService
{
    private const string MarkdownProviderId = "builtin.markdown";

    private readonly HostGateway _gateway;
    private readonly DataIoState _state;

    public ImportIdentityService(HostGateway gateway, DataIoState state)
    {
        _gateway = gateway;
        _state = state;
        _state.ImportLinks ??= new Dictionary<string, ImportIdentityLink>(StringComparer.Ordinal);
    }

    /// <summary>本次解析或记录是否修改了映射，调用方据此决定是否保存状态。</summary>
    public bool HasChanges { get; private set; }

    public bool ConsumeChanges()
    {
        var changed = HasChanges;
        HasChanges = false;
        return changed;
    }

    /// <summary>
    /// 为导入计划解析更新目标：命中映射且目标仍有效时写入 UpdateTargetId。
    /// 同时清理指向不存在/类型不符纸片的失效映射。
    /// </summary>
    public async Task ResolveAsync(ImportPlan plan, CancellationToken cancellationToken)
    {
        if (_state.ImportLinks.Count == 0 ||
            !_gateway.Can(PaperTodoPermissionNames.PapersRead))
        {
            return;
        }

        var papers = await _gateway.OnUiAsync(
            () => _gateway.Context.Workspace.ListPapers());
        var papersById = papers.ToDictionary(p => p.Id, StringComparer.Ordinal);

        Dictionary<string, TodoSnapshot[]> todosByPaper = new(StringComparer.Ordinal);
        if (_gateway.Can(PaperTodoPermissionNames.TodosRead))
        {
            var allTodos = await _gateway.OnUiAsync(
                () => _gateway.Context.Workspace.ListTodos());
            todosByPaper = allTodos
                .GroupBy(t => t.PaperId, StringComparer.Ordinal)
                .ToDictionary(
                    g => g.Key,
                    g => g.OrderBy(t => t.Order).ToArray(),
                    StringComparer.Ordinal);
        }

        foreach (var paper in plan.Papers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceId = paper.Source.SourceId;
            if (string.IsNullOrWhiteSpace(sourceId) ||
                !_state.ImportLinks.TryGetValue(sourceId, out var link))
            {
                continue;
            }

            var sourceType = NormalizeType(paper.Source.Type);
            var target = papersById.GetValueOrDefault(link.LocalPaperId);
            if (target is null ||
                !string.Equals(target.Type, sourceType, StringComparison.Ordinal))
            {
                // 当前目标已删除或类型不符：尝试回退到历史副本（例如“新增”后新副本被删除）。
                target = FindFallback(link, papersById, sourceType);
                if (target is null)
                {
                    _state.ImportLinks.Remove(sourceId);
                    HasChanges = true;
                    continue;
                }

                HasChanges = true;
            }

            string fingerprint;
            if (sourceType == "todo")
            {
                if (!_gateway.Can(PaperTodoPermissionNames.TodosRead))
                {
                    continue;
                }

                TodoSnapshot[] localTodos =
                    todosByPaper.TryGetValue(target.Id, out var todos) ? todos : [];
                fingerprint = ComputeTodoFingerprint(localTodos);
                paper.UpdateDiff = TextDiff.Compute(
                    TextDiff.TodoSnapshot(localTodos
                        .Where(todo => !string.IsNullOrWhiteSpace(todo.Text))
                        .OrderBy(todo => todo.Order)
                        .Select(todo => (todo.Text, todo.Done))),
                    TextDiff.TodoSnapshot((paper.Source.Todos ?? [])
                        .Where(todo => !string.IsNullOrWhiteSpace(todo.Text))
                        .OrderBy(todo => todo.Order)
                        .Select(todo => (todo.Text, todo.Done))));
                paper.UpdateHasChanges = paper.UpdateDiff.Any(line => line.Kind is '-' or '+');
            }
            else
            {
                if (!string.Equals(target.BodyProviderId, MarkdownProviderId, StringComparison.Ordinal))
                {
                    // 目标已不再是内置 Markdown 笔记：先尝试回退到同为 Markdown 的历史副本，否则清理映射。
                    target = FindFallback(
                        link,
                        papersById,
                        sourceType,
                        requireMarkdownProvider: true);
                    if (target is null)
                    {
                        _state.ImportLinks.Remove(sourceId);
                        HasChanges = true;
                        plan.Warnings.Add(
                            $"纸片“{DisplayTitle(paper)}”的更新目标不再是内置 Markdown 笔记，已取消关联。");
                        continue;
                    }

                    HasChanges = true;
                }

                if (!_gateway.Can(PaperTodoPermissionNames.NotesRead))
                {
                    // 权限缺失只是暂时无法读取，保留映射，等权限恢复后继续更新。
                    continue;
                }

                var note = await _gateway.OnUiAsync(
                    () => _gateway.Context.Workspace.GetNote(target.Id));
                if (note is not { ContentAvailable: true })
                {
                    plan.Warnings.Add(
                        $"纸片“{DisplayTitle(paper)}”的本地笔记正文暂不可读，本次跳过更新。");
                    continue;
                }

                fingerprint = ComputeNoteFingerprint(note.Content);
                paper.UpdateDiff = TextDiff.Compute(note.Content, paper.Source.Content ?? "");

                // 等价判断忽略尾部空行与分隔线，与真正写回时的判断保持一致。
                paper.UpdateHasChanges = !MarkdownNormalize.ContentEquivalent(
                    note.Content,
                    paper.Source.Content);

                // 本地仍有图片引用、而导入正文已是旧版“图片缺失”标记时，更新会破坏图片引用。
                paper.TargetImagesAtRisk =
                    ImageReferenceScanner.Count(note.Content) > 0 &&
                    ImageReferenceScanner.ContainsMissingLabel(
                        paper.Source.Content,
                        MarkdownExportService.MissingImageLabel);
            }

            paper.UpdateTargetFingerprint = fingerprint;
            paper.UpdateTargetId = target.Id;
            paper.UpdateTargetTitle = target.Title;
            paper.TargetLocallyModified =
                link.LocalFingerprint.Length > 0 &&
                !string.Equals(link.LocalFingerprint, fingerprint, StringComparison.Ordinal);
        }
    }

    /// <summary>导入（含顺序/关联/提醒修复）完成后，记录成功纸片的映射与本地指纹。</summary>
    public async Task RecordAsync(
        IEnumerable<ImportPaper> papers,
        IReadOnlyDictionary<string, string> sourceIdToPaperId,
        CancellationToken cancellationToken)
    {
        foreach (var paper in papers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceId = paper.Source.SourceId;
            if (string.IsNullOrWhiteSpace(sourceId) ||
                !sourceIdToPaperId.TryGetValue(sourceId, out var localId))
            {
                continue;
            }

            var type = NormalizeType(paper.Source.Type);
            string? fingerprint;
            try
            {
                fingerprint = await ComputeLocalFingerprintAsync(localId, type, cancellationToken);
            }
            catch (PaperTodoPluginException)
            {
                // 单张纸片读取失败不能影响其余映射的登记。
                continue;
            }

            if (fingerprint == null)
            {
                continue;
            }

            _state.ImportLinks.TryGetValue(sourceId, out var previous);
            _state.ImportLinks[sourceId] = new ImportIdentityLink
            {
                LocalPaperId = localId,
                Type = type,
                Title = paper.Source.Title,
                LocalFingerprint = fingerprint,
                LastImportedAt = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture),
                PreviousLocalPaperIds = MergeHistory(
                    previous?.LocalPaperId,
                    previous?.PreviousLocalPaperIds,
                    localId)
            };
            HasChanges = true;
        }
    }

    /// <summary>
    /// 计算新的历史副本列表：目标从旧副本切换到新副本时，把旧副本压入历史；
    /// 更新同一目标时保持历史不变。
    /// </summary>
    internal static List<string> MergeHistory(
        string? previousTarget,
        IEnumerable<string>? previousHistory,
        string newTarget)
    {
        var history = previousHistory?.ToList() ?? [];

        if (!string.IsNullOrWhiteSpace(previousTarget) &&
            !string.Equals(previousTarget, newTarget, StringComparison.Ordinal))
        {
            history.RemoveAll(id =>
                string.IsNullOrWhiteSpace(id) ||
                string.Equals(id, newTarget, StringComparison.Ordinal) ||
                string.Equals(id, previousTarget, StringComparison.Ordinal));
            history.Insert(0, previousTarget);
            if (history.Count > DataIoState.MaxImportLinkHistory)
            {
                history.RemoveRange(
                    DataIoState.MaxImportLinkHistory,
                    history.Count - DataIoState.MaxImportLinkHistory);
            }
        }

        return history;
    }

    /// <summary>
    /// 宿主删除纸片后清理对应映射。若删除的是当前目标，则回退到最近的历史副本，
    /// 这样“选择新增后删除新副本，再导入同一文件”仍能匹配到原副本并显示差异。
    /// </summary>
    public void RemoveByLocalPaperId(string paperId)
    {
        var staleSources = new List<string>();

        foreach (var (sourceId, link) in _state.ImportLinks)
        {
            var history = link.PreviousLocalPaperIds ?? [];
            if (history.RemoveAll(id => string.Equals(id, paperId, StringComparison.Ordinal)) > 0)
            {
                HasChanges = true;
            }

            if (!string.Equals(link.LocalPaperId, paperId, StringComparison.Ordinal))
            {
                link.PreviousLocalPaperIds = history;
                continue;
            }

            HasChanges = true;
            if (history.Count > 0)
            {
                // 当前目标被删除：回退到最近一个仍存在的历史副本。
                link.LocalPaperId = history[0];
                history.RemoveAt(0);
                link.PreviousLocalPaperIds = history;
                // 历史副本的旧指纹未知，留空以避免误报“本地已修改”；下次导入后重新登记。
                link.LocalFingerprint = "";
            }
            else
            {
                staleSources.Add(sourceId);
            }
        }

        foreach (var sourceId in staleSources)
        {
            _state.ImportLinks.Remove(sourceId);
        }
    }

    private async Task<string?> ComputeLocalFingerprintAsync(
        string paperId,
        string type,
        CancellationToken cancellationToken)
    {
        if (type == "todo")
        {
            if (!_gateway.Can(PaperTodoPermissionNames.TodosRead))
            {
                return null;
            }

            var todos = await _gateway.OnUiAsync(
                () => _gateway.Context.Workspace.ListTodos(paperId, includeBlank: true));
            cancellationToken.ThrowIfCancellationRequested();
            return ComputeTodoFingerprint(todos);
        }

        if (!_gateway.Can(PaperTodoPermissionNames.NotesRead))
        {
            return null;
        }

        var note = await _gateway.OnUiAsync(
            () => _gateway.Context.Workspace.GetNote(paperId));
        cancellationToken.ThrowIfCancellationRequested();
        return note is { ContentAvailable: true }
            ? ComputeNoteFingerprint(note.Content)
            : null;
    }

    /// <summary>
    /// 从历史副本中找出最近一个仍存在且类型一致的纸片；命中后把它提升为当前目标。
    /// 历史副本的旧指纹未知，清空以避免误报“本地已修改”。
    /// </summary>
    internal static PaperSnapshot? FindFallback(
        ImportIdentityLink link,
        IReadOnlyDictionary<string, PaperSnapshot> papersById,
        string type,
        bool requireMarkdownProvider = false)
    {
        var history = link.PreviousLocalPaperIds ?? [];
        while (history.Count > 0)
        {
            var candidateId = history[0];
            history.RemoveAt(0);
            if (papersById.TryGetValue(candidateId, out var candidate) &&
                string.Equals(candidate.Type, type, StringComparison.Ordinal) &&
                (!requireMarkdownProvider ||
                 string.Equals(candidate.BodyProviderId, MarkdownProviderId, StringComparison.Ordinal)))
            {
                link.LocalPaperId = candidateId;
                link.LocalFingerprint = "";
                link.PreviousLocalPaperIds = history;
                return candidate;
            }
        }

        link.PreviousLocalPaperIds = history;
        return null;
    }

    private static string DisplayTitle(ImportPaper paper) =>
        string.IsNullOrWhiteSpace(paper.Source.Title) ? "（无标题）" : paper.Source.Title;

    public static string ComputeNoteFingerprint(string? content) =>
        Hash(content ?? "");

    public static string ComputeTodoFingerprint(IEnumerable<TodoSnapshot> todos)
    {
        var builder = new StringBuilder();
        foreach (var todo in todos
                     .Where(todo => !string.IsNullOrWhiteSpace(todo.Text))
                     .OrderBy(todo => todo.Order))
        {
            builder
                .Append(todo.Text).Append('\u001f')
                .Append(todo.Done ? '1' : '0').Append('\u001f')
                .Append(todo.ReminderAt?.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) ?? "")
                .Append('\u001f')
                .Append(todo.LinkedPaperId ?? "").Append('\u001f')
                .Append(todo.LinkedPath ?? "").Append('\u001f')
                .Append(todo.LinkedPathIsDirectory?.ToString() ?? "")
                .Append('\u001e');
        }

        return Hash(builder.ToString());
    }

    private static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))
            .ToLowerInvariant();

    private static string NormalizeType(string? type) =>
        string.Equals(type, "note", StringComparison.OrdinalIgnoreCase) ? "note" : "todo";
}
