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

    private readonly HostGateway _gateway;
    private readonly DataIoState _state;

    /// <summary>
    /// 按 ID 读取纸片快照，用于删除事件中的回退校验。
    /// 返回 null 表示确认纸片不存在；抛 PaperTodoPluginException 表示暂时无法查询。
    /// 为 null 时退化为不校验（仅测试这样构造）。
    /// </summary>
    private readonly Func<string, PaperSnapshot?>? _paperLookup;

    public ImportIdentityService(
        HostGateway gateway,
        DataIoState state,
        Func<string, PaperSnapshot?>? paperLookup = null)
    {
        _gateway = gateway;
        _state = state;
        _paperLookup = paperLookup;
        _state.ImportLinks ??= new Dictionary<string, ImportIdentityLink>(StringComparer.Ordinal);
        _state.RemovedLinks ??= new Dictionary<string, string>(StringComparer.Ordinal);
    }

    /// <summary>
    /// 记录一次删除墓碑。Runtime 的合并协议无法表达删除，没有墓碑时
    /// 本地删掉的映射会被下一次权威快照原样复活。
    /// </summary>
    private void MarkRemoved(string sourceId) =>
        _state.RemovedLinks[sourceId] =
            DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture);

    /// <summary>映射被本地修改后的时间戳，让 Runtime 合并时采用本地较新的版本。</summary>
    private static string TouchTimestamp() =>
        DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture);

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
    public Task ResolveAsync(ImportPlan plan, CancellationToken cancellationToken) =>
        ResolveAsync(plan, new DataIoSettings(), cancellationToken);

    public async Task ResolveAsync(
        ImportPlan plan,
        DataIoSettings settings,
        CancellationToken cancellationToken)
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
                    MarkRemoved(sourceId);
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
                paper.UpdateHasMetaChanges = HasTodoMetaChanges(
                    localTodos,
                    paper.Source.Todos,
                    settings,
                    plan.SupportsPaperLinks,
                    plan.SupportsReminders);
            }
            else
            {
                if (!string.Equals(target.BodyProviderId, PaperProviders.Markdown, StringComparison.Ordinal))
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
                        MarkRemoved(sourceId);
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

                // Markdown 包的正文用 images/ 链接，本地用 i:ID；图片内容一致时只是写法不同。
                // 等价判断同时忽略行尾空行、分隔线和图片地址格式，避免把“无变化”误报成待更新。
                paper.UpdateHasChanges = !await NoteContentEquivalentAsync(
                    target.Id,
                    note.Content,
                    paper);

                // 本地仍有图片引用、而导入正文已是旧版“图片缺失”标记时，更新会破坏图片引用。
                // 另外，压缩包缺少图片或图片超过写入上限时同样不能更新，否则源 ID 可能指向本机另一张图。
                paper.TargetImagesAtRisk =
                    (ImageReferenceScanner.Count(note.Content) > 0 &&
                     ImageReferenceScanner.ContainsMissingLabel(
                         paper.Source.Content,
                         MarkdownExportService.MissingImageLabel)) ||
                    ImportService.GetImageRestoreBlocker(paper) != null;
            }

            paper.UpdateTargetFingerprint = fingerprint;
            paper.UpdateTargetId = target.Id;
            paper.UpdateTargetTitle = target.Title;
            paper.TargetLocallyModified =
                !link.LocalFingerprintUnknown &&
                link.LocalFingerprint.Length > 0 &&
                !string.Equals(link.LocalFingerprint, fingerprint, StringComparison.Ordinal);
            paper.TargetLocalStateUnknown = link.LocalFingerprintUnknown;
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
            string? fingerprint = null;
            try
            {
                fingerprint = await ComputeLocalFingerprintAsync(localId, type, cancellationToken);
            }
            catch (PaperTodoPluginException)
            {
                // 纸片已随会话销毁、正在重载或权限暂时不可用时读不到指纹。
                // 但导入已经写入纸片，映射必须照常登记：否则下次导入会把它当成新建副本。
                // 指纹记为未知，更新预览会保守提示“无法确认本地是否修改”。
            }

            _state.ImportLinks.TryGetValue(sourceId, out var previous);
            _state.ImportLinks[sourceId] = new ImportIdentityLink
            {
                LocalPaperId = localId,
                Type = type,
                Title = paper.Source.Title,
                LocalFingerprint = fingerprint ?? "",
                LocalFingerprintUnknown = fingerprint is null,
                LastImportedAt = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture),
                PreviousLocalPaperIds = MergeHistory(
                    previous?.LocalPaperId,
                    previous?.PreviousLocalPaperIds,
                    localId)
            };
            // 重新导入成功的映射必须撤销旧墓碑，否则墓碑会把新映射再删掉。
            _state.RemovedLinks.Remove(sourceId);
            HasChanges = true;
        }

        if (Trim(_state.ImportLinks))
        {
            HasChanges = true;
        }
    }

    /// <summary>
    /// 映射条目超过上限时按最近导入时间淘汰最旧条目，返回是否发生了淘汰。
    /// 上限若只在加载时截断，运行期超限的映射会在下次重启时被静默丢弃，这里提前收敛。
    /// </summary>
    internal static bool Trim(Dictionary<string, ImportIdentityLink> links)
    {
        var overflow = links.Count - DataIoState.MaxImportLinks;
        if (overflow <= 0)
        {
            return false;
        }

        var oldest = links
            .OrderBy(pair => ParseImportedAt(pair.Value.LastImportedAt))
            .Take(overflow)
            .Select(pair => pair.Key)
            .ToArray();
        foreach (var sourceId in oldest)
        {
            links.Remove(sourceId);
        }

        return oldest.Length > 0;
    }

    /// <summary>比较两条映射的最近导入时间；无法解析时按最旧处理。</summary>
    internal static int CompareImportedAt(string? left, string? right) =>
        ParseImportedAt(left).CompareTo(ParseImportedAt(right));

    private static DateTimeOffset ParseImportedAt(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;

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
    /// PaperTodo 删除纸片后清理对应映射。若删除的是当前目标，则回退到最近一个仍存在的历史副本，
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
                // 历史副本变化也要让 Runtime 采用本地版本：合并按 LastImportedAt 取新，
                // 不改时间戳的修改会被权威快照原样回滚。
                link.LastImportedAt = TouchTimestamp();
            }

            if (!string.Equals(link.LocalPaperId, paperId, StringComparison.Ordinal))
            {
                link.PreviousLocalPaperIds = history;
                continue;
            }

            HasChanges = true;

            // 当前目标被删除：回退到最近一个仍存在的历史副本。
            // 历史副本可能在此前事件丢失的窗口（如工具纸片隐藏期间）里也被删除，
            // 因此有查询能力时必须逐个确认，避免把映射指向已经不存在的纸片。
            var promoted = false;
            try
            {
                while (history.Count > 0)
                {
                    var candidateId = history[0];
                    if (_paperLookup is { } lookup)
                    {
                        var candidate = lookup(candidateId);
                        if (candidate is null ||
                            !string.Equals(candidate.Type, link.Type, StringComparison.Ordinal))
                        {
                            // 确认已不存在或类型不符：丢弃该候选继续找下一个。
                            history.RemoveAt(0);
                            continue;
                        }
                    }

                    history.RemoveAt(0);
                    link.LocalPaperId = candidateId;
                    link.PreviousLocalPaperIds = history;
                    link.LastImportedAt = TouchTimestamp();
                    // 历史副本没有导入时的指纹：标记为未知，
                    // 更新预览必须保守提示“可能已修改”，不能静默当作无修改。
                    link.LocalFingerprint = "";
                    link.LocalFingerprintUnknown = true;
                    promoted = true;
                    break;
                }
            }
            catch (PaperTodoPluginException)
            {
                // 暂时无法查询纸片：保留映射与剩余历史，下次导入时再校验，不能据此删除映射。
                link.PreviousLocalPaperIds = history;
                continue;
            }

            if (!promoted)
            {
                staleSources.Add(sourceId);
            }
        }

        foreach (var sourceId in staleSources)
        {
            _state.ImportLinks.Remove(sourceId);
            MarkRemoved(sourceId);
        }
    }

    /// <summary>
    /// 用当前纸片列表对账映射：目标或历史副本已被删除时回退或清理。
    /// PaperTodo 只在工具纸片可见时投递变更事件，纸片隐藏期间的删除可能从未送达，
    /// 因此每次刷新列表时主动校验一次，而不是完全依赖删除事件。
    /// 类型与正文 provider 的校验仍留给导入时的 <see cref="ResolveAsync"/>，那里能给出明确提示。
    /// </summary>
    public void Reconcile(IReadOnlyDictionary<string, PaperSnapshot> papersById)
    {
        if (_state.ImportLinks.Count == 0)
        {
            return;
        }

        var staleSources = new List<string>();
        foreach (var (sourceId, link) in _state.ImportLinks)
        {
            if (papersById.ContainsKey(link.LocalPaperId))
            {
                // 目标仍存在：只清理历史中已不存在的条目，避免死 ID 长期占用回退名额。
                var history = link.PreviousLocalPaperIds ?? [];
                if (history.RemoveAll(id => !papersById.ContainsKey(id)) > 0)
                {
                    link.PreviousLocalPaperIds = history;
                    // 让 Runtime 在合并时采用这次修剪，而不是用旧快照回滚。
                    link.LastImportedAt = TouchTimestamp();
                    HasChanges = true;
                }

                continue;
            }

            // 目标已删除：回退到最近仍存在的历史副本，找不到则清理映射。
            HasChanges = true;
            if (FindFallback(link, papersById, link.Type) is null)
            {
                staleSources.Add(sourceId);
            }
        }

        foreach (var sourceId in staleSources)
        {
            _state.ImportLinks.Remove(sourceId);
            MarkRemoved(sourceId);
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
                 string.Equals(candidate.BodyProviderId, PaperProviders.Markdown, StringComparison.Ordinal)))
            {
                link.LocalPaperId = candidateId;
                link.LocalFingerprint = "";
                link.LocalFingerprintUnknown = true;
                link.PreviousLocalPaperIds = history;
                // 目标切换也是一次本地修改：更新导入时间以便 Runtime 合并时采用。
                link.LastImportedAt = TouchTimestamp();
                return candidate;
            }
        }

        link.PreviousLocalPaperIds = history;
        return null;
    }

    private static string DisplayTitle(ImportPaper paper) =>
        string.IsNullOrWhiteSpace(paper.Source.Title) ? "（无标题）" : paper.Source.Title;

    /// <summary>
    /// 笔记等价判断：忽略图片地址写法（本地 i:ID / 文件 images/ 链接），
    /// 并按出现顺序比较图片内容哈希。只有正文与图片都一致才算“无变化”。
    /// </summary>
    private async Task<bool> NoteContentEquivalentAsync(
        string paperId,
        string? localContent,
        ImportPaper paper)
    {
        var source = paper.Source.Content ?? "";
        localContent ??= "";
        if (MarkdownNormalize.ContentEquivalent(localContent, source))
        {
            // 文件声明过、但无法写回的图片引用需要由“更新”移除；
            // 否则逐字相同的正文会被当成无变化，坏引用永远留在本地。
            return !ImportService.HasUnrestorableDeclaredReferences(paper);
        }

        // 没有图片包时无法确认图片是否一致，保守地按有变化处理。
        if (paper.Images.Count == 0)
        {
            return false;
        }

        if (!MarkdownNormalize.ContentEquivalent(
                ImageReferenceScanner.NormalizeImageUrls(localContent),
                ImageReferenceScanner.NormalizeImageUrls(source)))
        {
            return false;
        }

        if (!_gateway.Can(PaperTodoPermissionNames.NotesRead))
        {
            return false;
        }

        var sourceHashes = SourceImageHashes(paper);
        if (sourceHashes is null)
        {
            return false;
        }

        var localHashes = await LocalImageHashesAsync(paperId, localContent);
        return localHashes is not null &&
               sourceHashes.SequenceEqual(localHashes, StringComparer.Ordinal);
    }

    /// <summary>按正文出现顺序列出文件内图片的内容哈希；有引用无法解析时返回 null。</summary>
    private static List<string>? SourceImageHashes(ImportPaper paper)
    {
        var content = paper.Source.Content ?? "";
        var result = new List<string>();
        if (paper.UsesRelativeImageLinks)
        {
            var byFile = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var image in paper.Images)
            {
                byFile.TryAdd(image.File, Sha256(image.Bytes));
            }

            foreach (var link in ImageReferenceScanner.EnumerateRelativeImageLinks(content))
            {
                if (!byFile.TryGetValue(link, out var hash))
                {
                    return null;
                }

                result.Add(hash);
            }

            return result;
        }

        var byId = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var image in paper.Images)
        {
            byId.TryAdd(image.SourceImageId, Sha256(image.Bytes));
        }

        foreach (var reference in ImageReferenceScanner.Enumerate(content))
        {
            if (!byId.TryGetValue(reference.ImageId, out var hash))
            {
                return null;
            }

            result.Add(hash);
        }

        return result;
    }

    /// <summary>按正文出现顺序读取本地图片并计算哈希；读取失败或数量过多时返回 null。</summary>
    private async Task<List<string>?> LocalImageHashesAsync(string paperId, string content)
    {
        const int maxImages = 40;
        var references = ImageReferenceScanner.Enumerate(content);
        if (references.Count > maxImages)
        {
            return null;
        }

        var cache = new Dictionary<string, string?>(StringComparer.Ordinal);
        var result = new List<string>(references.Count);
        foreach (var reference in references)
        {
            if (!cache.TryGetValue(reference.ImageId, out var hash))
            {
                try
                {
                    var image = await _gateway.OnUiAsync(
                        () => _gateway.Context.NoteAssets.ReadImage(paperId, reference.ImageId));
                    hash = Sha256(image.Bytes);
                }
                catch (Exception ex) when (ex is PaperTodoPluginException or InvalidOperationException)
                {
                    hash = null;
                }

                cache[reference.ImageId] = hash;
            }

            if (hash is null)
            {
                return null;
            }

            result.Add(hash);
        }

        return result;
    }

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>
    /// 待办文本/完成状态之外的差异：顺序、提醒、关联纸片。
    /// 这些字段不在文本 diff 中，但更新时会被同步，因此不能仅凭 diff 判定“无变化”。
    /// </summary>
    internal static bool HasTodoMetaChanges(
        IReadOnlyList<TodoSnapshot> localTodos,
        IReadOnlyList<ExportTodo>? sourceTodos,
        DataIoSettings settings,
        bool supportsPaperLinks,
        bool supportsReminders)
    {
        if (!settings.RestoreOrder && !settings.RestoreLinks && !settings.RestoreReminders)
        {
            return false;
        }

        var local = localTodos
            .Where(todo => !string.IsNullOrWhiteSpace(todo.Text))
            .OrderBy(todo => todo.Order)
            .ToList();
        var source = (sourceTodos ?? [])
            .Where(todo => !string.IsNullOrWhiteSpace(todo.Text))
            .OrderBy(todo => todo.Order)
            .ToList();
        if (source.Count == 0)
        {
            return false;
        }

        var queues = local
            .GroupBy(todo => (todo.Text, todo.Done))
            .ToDictionary(
                group => group.Key,
                group => new Queue<TodoSnapshot>(group.OrderBy(todo => todo.Order)));
        var positions = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < local.Count; index++)
        {
            positions[local[index].Id] = index;
        }

        var matched = new List<(ExportTodo Source, TodoSnapshot Local)>(source.Count);
        var matchedPositions = new List<int>(source.Count);
        foreach (var item in source)
        {
            if (!queues.TryGetValue((item.Text, item.Done), out var queue) || queue.Count == 0)
            {
                continue;
            }

            var match = queue.Dequeue();
            matched.Add((item, match));
            matchedPositions.Add(positions[match.Id]);
        }

        if (settings.RestoreOrder)
        {
            for (var index = 1; index < matchedPositions.Count; index++)
            {
                if (matchedPositions[index] < matchedPositions[index - 1])
                {
                    return true;
                }
            }
        }

        if (settings.RestoreReminders && supportsReminders)
        {
            var now = DateTimeOffset.Now;
            foreach (var (item, match) in matched)
            {
                if (item.Done || match.Done)
                {
                    continue;
                }

                if (item.ReminderAt is { } reminder)
                {
                    if (reminder > now && match.ReminderAt != reminder)
                    {
                        return true;
                    }
                }
                else if (match.ReminderAt != null)
                {
                    return true;
                }
            }
        }

        if (settings.RestoreLinks && supportsPaperLinks)
        {
            foreach (var (item, match) in matched)
            {
                if (!string.IsNullOrWhiteSpace(item.LinkedSourcePaperId))
                {
                    if (!string.Equals(
                            match.LinkedPaperId,
                            item.LinkedSourcePaperId,
                            StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
                else if (!string.IsNullOrWhiteSpace(match.LinkedPaperId) &&
                         string.IsNullOrWhiteSpace(match.LinkedPath))
                {
                    return true;
                }
            }
        }

        return false;
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
