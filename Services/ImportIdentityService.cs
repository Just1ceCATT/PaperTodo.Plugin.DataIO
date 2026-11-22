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
            if (!papersById.TryGetValue(link.LocalPaperId, out var target) ||
                !string.Equals(target.Type, sourceType, StringComparison.Ordinal))
            {
                _state.ImportLinks.Remove(sourceId);
                HasChanges = true;
                continue;
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
            }
            else
            {
                if (!string.Equals(target.BodyProviderId, MarkdownProviderId, StringComparison.Ordinal) ||
                    !_gateway.Can(PaperTodoPermissionNames.NotesRead))
                {
                    continue;
                }

                var note = await _gateway.OnUiAsync(
                    () => _gateway.Context.Workspace.GetNote(target.Id));
                if (note is not { ContentAvailable: true })
                {
                    continue;
                }

                fingerprint = ComputeNoteFingerprint(note.Content);
                paper.UpdateDiff = TextDiff.Compute(note.Content, paper.Source.Content ?? "");
            }

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
            var fingerprint = await ComputeLocalFingerprintAsync(localId, type, cancellationToken);
            if (fingerprint == null)
            {
                continue;
            }

            _state.ImportLinks[sourceId] = new ImportIdentityLink
            {
                LocalPaperId = localId,
                Type = type,
                Title = paper.Source.Title,
                LocalFingerprint = fingerprint,
                LastImportedAt = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture)
            };
            HasChanges = true;
        }
    }

    /// <summary>宿主删除纸片后清理对应映射。</summary>
    public void RemoveByLocalPaperId(string paperId)
    {
        var stale = _state.ImportLinks
            .Where(pair => string.Equals(pair.Value.LocalPaperId, paperId, StringComparison.Ordinal))
            .Select(pair => pair.Key)
            .ToArray();
        foreach (var sourceId in stale)
        {
            _state.ImportLinks.Remove(sourceId);
            HasChanges = true;
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
