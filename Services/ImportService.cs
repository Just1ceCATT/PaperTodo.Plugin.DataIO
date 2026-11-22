using PaperTodo.Plugin;

namespace PaperTodo.Plugin.DataIo;

/// <summary>
/// 执行导入：第一遍逐纸片创建，第二遍还原顺序、待办关联纸片与提醒。
/// 第一遍不把关联纸片和提醒放进初始待办，避免宿主校验失败导致整张纸片创建失败。
/// </summary>
internal sealed class ImportService
{
    private const int MaxPapers = ImportPlanParser.MaxPapers;

    private readonly HostGateway _gateway;
    private readonly IProgress<string>? _progress;

    public ImportService(HostGateway gateway, IProgress<string>? progress = null)
    {
        _gateway = gateway;
        _progress = progress;
    }

    public async Task<ImportResult> ImportAsync(
        ImportPlan plan,
        DataIoSettings settings,
        CancellationToken cancellationToken)
    {
        var result = new ImportResult();

        if (!_gateway.Can(PaperTodoPermissionNames.PapersCreate))
        {
            result.FatalError = "缺少 papers.create 权限，无法创建纸片。";
            return result;
        }

        int existingCount;
        try
        {
            var existing = await _gateway.OnUiAsync(() => _gateway.Context.Workspace.ListPapers());
            existingCount = existing.Count;
        }
        catch (PaperTodoPluginException ex)
        {
            result.FatalError = $"读取现有纸片失败：{ex.Message}";
            return result;
        }

        var newPaperCount = plan.Papers.Count(
            paper => string.IsNullOrWhiteSpace(paper.UpdateTargetId));
        if (existingCount + newPaperCount > MaxPapers)
        {
            result.FatalError =
                $"当前已有 {existingCount} 张纸片，再新建 {newPaperCount} 张将超过宿主 {MaxPapers} 张上限。";
            return result;
        }

        var idMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var processed = new List<(ImportPaper Paper, string LocalId, bool Created)>();

        try
        {
            // 第一遍：逐张新建或更新纸片。
            for (var i = 0; i < plan.Papers.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var importPaper = plan.Papers[i];
                _progress?.Report($"正在导入 {i + 1}/{plan.Papers.Count}：{DisplayTitle(importPaper)}");

                try
                {
                    var createdNow = string.IsNullOrWhiteSpace(importPaper.UpdateTargetId);
                    string localId;
                    if (createdNow)
                    {
                        localId = await CreateOneAsync(importPaper, settings, result);
                    }
                    else
                    {
                        (localId, createdNow) = await UpdateOneAsync(
                            importPaper,
                            importPaper.UpdateTargetId!,
                            settings,
                            result,
                            cancellationToken);
                    }

                    processed.Add((importPaper, localId, createdNow));
                    if (createdNow)
                    {
                        result.CreatedPapers++;
                        result.CreatedTodos += importPaper.Source.Todos?.Count ?? 0;
                    }
                    else
                    {
                        result.UpdatedPapers++;
                        result.UpdatedTodos += importPaper.Source.Todos?.Count ?? 0;
                    }

                    if (!string.IsNullOrWhiteSpace(importPaper.Source.SourceId))
                    {
                        idMap[importPaper.Source.SourceId] = localId;
                        result.SourceIdToPaperId[importPaper.Source.SourceId] = localId;
                    }
                }
                catch (PaperTodoPluginException ex) when (IsFatal(ex.Code))
                {
                    result.FailedPapers++;
                    result.Failures.Add(Failure(i, importPaper, ex));
                    result.StoppedEarly = true;
                    result.FatalError = $"导入被宿主中止（{ex.Code}）：{ex.Message}";
                    break;
                }
                catch (PaperTodoPluginException ex)
                {
                    result.FailedPapers++;
                    result.Failures.Add(Failure(i, importPaper, ex));
                }
                catch (Exception ex)
                {
                    result.FailedPapers++;
                    result.Failures.Add(new ImportFailure(i + 1, DisplayTitle(importPaper), importPaper.Source.Type, "unknown", ex.Message));
                }

                await HostGateway.YieldAsync();
            }

            // 第二遍：补顺序、关联纸片、提醒。
            if (!result.StoppedEarly)
            {
                for (var i = 0; i < processed.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var (paper, localId, _) = processed[i];
                    _progress?.Report($"正在还原 {i + 1}/{processed.Count}：{DisplayTitle(paper)}");
                    await RepairAsync(paper, localId, settings, idMap, result, cancellationToken);
                    await HostGateway.YieldAsync();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 宿主没有事务回滚能力：已完成的部分已经写入，必须如实标记为“部分完成”，
            // 由调用方提示用户，而不是让取消看起来像什么都没发生。
            result.Cancelled = true;
        }

        return result;
    }

    /// <summary>
    /// 更新身份映射命中的本地纸片：笔记整体替换，待办按文本做增删改。
    /// 标题、类型等宿主不支持的字段保持不变，并在标题不一致时提示。
    /// 所有可预见的失败（权限、内容不可用、破坏性差异）都在写入前判定，避免部分生效。
    /// </summary>
    private async Task<(string Id, bool Created)> UpdateOneAsync(
        ImportPaper importPaper,
        string targetId,
        DataIoSettings settings,
        ImportResult result,
        CancellationToken cancellationToken)
    {
        var source = importPaper.Source;
        var type = (source.Type ?? "").Trim().ToLowerInvariant();

        var target = await _gateway.OnUiAsync(
            () => _gateway.Context.Workspace.GetPaper(targetId));
        if (target == null ||
            !string.Equals(target.Type, type, StringComparison.Ordinal))
        {
            result.Warnings.Add(
                $"纸片“{DisplayTitle(importPaper)}”的更新目标已失效，已改为新建。");
            var fallbackId = await CreateOneAsync(importPaper, settings, result);
            return (fallbackId, true);
        }

        if (!string.IsNullOrWhiteSpace(importPaper.SafeTitle) &&
            !string.Equals(target.Title, importPaper.SafeTitle, StringComparison.Ordinal))
        {
            result.Warnings.Add(
                $"纸片“{DisplayTitle(importPaper)}”的标题与本地不一致，宿主不支持重命名，已保留本地标题。");
        }

        if (type == "note")
        {
            return await UpdateNoteAsync(importPaper, targetId);
        }

        return await UpdateTodoPaperAsync(importPaper, targetId, result, cancellationToken);
    }

    /// <summary>更新笔记纸片：整体替换正文，但绝不接受会把本地图片引用写坏的来源。</summary>
    private async Task<(string Id, bool Created)> UpdateNoteAsync(
        ImportPaper importPaper,
        string targetId)
    {
        var source = importPaper.Source;

        // 只信任明确标记为“正文可读”的笔记；缺失该字段的文件不能用来清空本地正文。
        if (source.ContentAvailable != true)
        {
            throw new PaperTodoPluginException("content_unavailable", "文件未包含可用的笔记正文，已跳过更新。");
        }

        if (!_gateway.Can(PaperTodoPermissionNames.NotesReplace))
        {
            throw new PaperTodoPluginException("permission_denied", "缺少 notes.replace 权限，无法更新笔记。");
        }

        // 旧版 Markdown 导出会把图片引用替换为缺失标记，回导更新会永久破坏本地图片引用。
        if (ImageReferenceScanner.ContainsMissingLabel(
                source.Content,
                MarkdownExportService.MissingImageLabel))
        {
            throw new PaperTodoPluginException(
                "lossy_update_blocked",
                "导入文件中的图片引用已被替换为“图片缺失”标记，更新会破坏本地图片引用，已跳过该纸片；" +
                "请改用 JSON 文件，或在更新预览中为它选择“新增”。");
        }

        var current = await TryGetNoteAsync(targetId);
        if (current is { ContentAvailable: true } &&
            ContentEquivalent(current.Content, source.Content))
        {
            // 正文等价（忽略行尾空行与末尾分隔线）时不写回，
            // 避免 Markdown 往返产生的规范化差异被持久化。
            return (targetId, false);
        }

        await _gateway.OnUiAsync(() => _gateway.Context.Workspace.WriteNote(new WriteNoteRequest
        {
            PaperId = targetId,
            Content = source.Content ?? "",
            Mode = NoteWriteMode.Replace
        }));
        await HostGateway.YieldAsync();
        return (targetId, false);
    }

    /// <summary>更新待办纸片：先完整匹配并校验权限，再统一写入，避免“改一半才报失败”。</summary>
    private async Task<(string Id, bool Created)> UpdateTodoPaperAsync(
        ImportPaper importPaper,
        string targetId,
        ImportResult result,
        CancellationToken cancellationToken)
    {
        if (!_gateway.Can(PaperTodoPermissionNames.TodosUpdate))
        {
            throw new PaperTodoPluginException("permission_denied", "缺少 todos.update 权限，无法更新待办纸片。");
        }

        var source = importPaper.Source;

        var existing = await _gateway.OnUiAsync(
            () => _gateway.Context.Workspace.ListTodos(targetId, includeBlank: true));
        var remaining = existing
            .Where(todo => !string.IsNullOrWhiteSpace(todo.Text))
            .GroupBy(todo => todo.Text, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(todo => todo.Order).ToList(),
                StringComparer.Ordinal);

        // 第一阶段只在内存中完成匹配，任何可预见的权限缺失都在写入前抛出。
        var doneChanges = new List<(string TodoId, bool Done)>();
        var toAppend = new List<ExportTodo>();
        foreach (var original in (source.Todos ?? []).OrderBy(todo => todo.Order))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(original.Text))
            {
                continue;
            }

            if (!remaining.TryGetValue(original.Text, out var candidates) || candidates.Count == 0)
            {
                toAppend.Add(original);
                continue;
            }

            var index = candidates.FindIndex(todo => todo.Done == original.Done);
            if (index < 0)
            {
                index = 0;
            }

            var match = candidates[index];
            candidates.RemoveAt(index);
            if (match.Done != original.Done)
            {
                doneChanges.Add((match.Id, original.Done));
            }
        }

        var removed = remaining.Values
            .SelectMany(todos => todos)
            .Where(todo => !string.IsNullOrWhiteSpace(todo.Text))
            .ToList();

        if (toAppend.Count > 0 && !_gateway.Can(PaperTodoPermissionNames.TodosAppend))
        {
            throw new PaperTodoPluginException("permission_denied", "缺少 todos.append 权限，无法追加待办。");
        }

        // 第二阶段：权限确认后统一写入。
        foreach (var (todoId, done) in doneChanges)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _gateway.OnUiAsync(() => _gateway.Context.Workspace.UpdateTodo(new UpdateTodoRequest
            {
                PaperId = targetId,
                TodoId = todoId,
                Done = done
            }));
        }

        if (toAppend.Count > 0)
        {
            await _gateway.OnUiAsync(() => _gateway.Context.Workspace.AppendTodos(new AppendTodosRequest
            {
                PaperId = targetId,
                Todos = toAppend
                    .Select(todo => new TodoCreateItem { Text = todo.Text, Done = todo.Done })
                    .ToArray()
            }));
        }

        if (removed.Count > 0)
        {
            if (_gateway.Can(PaperTodoPermissionNames.TodosDelete))
            {
                foreach (var todo in removed)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await _gateway.OnUiAsync(() => _gateway.Context.Workspace.DeleteTodo(
                        new DeleteTodoRequest { PaperId = targetId, TodoId = todo.Id }));
                    result.DeletedTodos++;
                }
            }
            else
            {
                result.Warnings.Add(
                    $"纸片“{DisplayTitle(importPaper)}”中有 {removed.Count} 条本地待办已不在文件中，" +
                    "缺少 todos.delete 权限，未删除。");
            }
        }

        await HostGateway.YieldAsync();
        return (targetId, false);
    }

    /// <summary>读取本地笔记正文；读取失败时返回 null，后续按普通更新处理。</summary>
    private async Task<NoteSnapshot?> TryGetNoteAsync(string paperId)
    {
        try
        {
            return await _gateway.OnUiAsync(
                () => _gateway.Context.Workspace.GetNote(paperId));
        }
        catch (PaperTodoPluginException)
        {
            return null;
        }
    }

    /// <summary>
    /// 忽略行尾空行与末尾分隔线的正文等价比较。
    /// Markdown 往返会剥离末尾的 --- 分隔线，等价内容不应再写回本地。
    /// </summary>
    private static bool ContentEquivalent(string? local, string? incoming)
    {
        static string Normalize(string? text)
        {
            var lines = (text ?? "")
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .Split('\n')
                .ToList();

            while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1]))
            {
                lines.RemoveAt(lines.Count - 1);
            }

            if (lines.Count > 0 && IsSeparatorLine(lines[^1]))
            {
                lines.RemoveAt(lines.Count - 1);
                while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1]))
                {
                    lines.RemoveAt(lines.Count - 1);
                }
            }

            return string.Join("\n", lines);
        }

        return string.Equals(Normalize(local), Normalize(incoming), StringComparison.Ordinal);
    }

    private static bool IsSeparatorLine(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Length >= 3 && trimmed.All(character => character == '-');
    }

    private async Task<string> CreateOneAsync(ImportPaper importPaper, DataIoSettings settings, ImportResult result)
    {
        var source = importPaper.Source;
        var type = (source.Type ?? "").Trim().ToLowerInvariant();

        if (type == "todo" && !_gateway.Can(PaperTodoPermissionNames.TodosAppend))
        {
            throw new PaperTodoPluginException("permission_denied", "缺少 todos.append 权限，无法导入待办纸片。");
        }

        if (type == "note" &&
            !string.IsNullOrEmpty(source.Content) &&
            !_gateway.Can(PaperTodoPermissionNames.NotesAppend))
        {
            throw new PaperTodoPluginException("permission_denied", "缺少 notes.append 权限，无法导入带正文的笔记。");
        }

        TodoCreateItem[]? todos = type == "todo"
            ? (source.Todos ?? [])
                .Where(t => !string.IsNullOrWhiteSpace(t.Text))
                .Select(t => new TodoCreateItem { Text = t.Text, Done = t.Done })
                .ToArray()
            : null;

        var request = new CreatePaperRequest
        {
            Type = type,
            Title = importPaper.SafeTitle,
            Show = settings.ImportShowCreated || source.Show,
            Content = type == "note" ? source.Content ?? "" : null,
            Todos = todos
        };

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var mutated = await _gateway.OnUiAsync(() => _gateway.Context.Workspace.CreatePaper(request));
                return mutated.PaperId;
            }
            catch (PaperTodoPluginException ex) when (
                attempt < 2 &&
                ex.Code == "invalid_params" &&
                TitleSanitizer.TryParseTitleLimit(ex.Message, out var limit))
            {
                // 宿主标题上限可按用户设置变化，从错误文案解析后截断重试。
                var normalized = TitleSanitizer.Normalize(request.Title);
                var truncated = TitleSanitizer.TruncateByDisplayWidth(
                    normalized,
                    Math.Clamp(limit, 1, TitleSanitizer.MaxConfiguredTitleLength) * 2);

                result.Warnings.Add($"标题“{normalized}”超过宿主上限，已截断为“{truncated}”。");
                request = request with { Title = truncated.Length == 0 ? null : truncated };
            }
        }
    }

    private async Task RepairAsync(
        ImportPaper importPaper,
        string newPaperId,
        DataIoSettings settings,
        Dictionary<string, string> idMap,
        ImportResult result,
        CancellationToken cancellationToken)
    {
        var source = importPaper.Source;
        if (!string.Equals(source.Type, "todo", StringComparison.OrdinalIgnoreCase) ||
            source.Todos is not { Count: > 0 })
        {
            return;
        }

        var wantedRepair = settings.RestoreOrder || settings.RestoreLinks || settings.RestoreReminders;
        if (!wantedRepair)
        {
            return;
        }

        if (!_gateway.Can(PaperTodoPermissionNames.TodosUpdate))
        {
            result.Warnings.Add("缺少 todos.update 权限，已跳过顺序、关联纸片和提醒还原。");
            return;
        }

        IReadOnlyList<TodoSnapshot> actual;
        try
        {
            actual = await _gateway.OnUiAsync(
                () => _gateway.Context.Workspace.ListTodos(newPaperId, includeBlank: true));
        }
        catch (PaperTodoPluginException ex)
        {
            result.Warnings.Add($"读取新纸片待办失败，未还原顺序/关联纸片/提醒：{ex.Message}");
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();

        // 按 (文本, 完成状态) 建立队列，避免宿主自动清除/置底后按索引错配。
        var queues = actual
            .Where(a => !string.IsNullOrWhiteSpace(a.Text))
            .GroupBy(a => (a.Text, a.Done))
            .ToDictionary(
                g => g.Key,
                g => new Queue<TodoSnapshot>(g.OrderBy(x => x.Order)));

        var matched = new List<(ExportTodo Original, TodoSnapshot Actual)>();
        foreach (var original in source.Todos.OrderBy(t => t.Order))
        {
            if (string.IsNullOrWhiteSpace(original.Text))
            {
                continue;
            }

            if (!queues.TryGetValue((original.Text, original.Done), out var queue) || queue.Count == 0)
            {
                result.Warnings.Add($"待办“{Shorten(original.Text)}”未能找回，可能被宿主自动清除。");
                continue;
            }

            matched.Add((original, queue.Dequeue()));
        }

        if (settings.RestoreOrder)
        {
            for (var i = 0; i < matched.Count; i++)
            {
                var order = i;
                var todoId = matched[i].Actual.Id;
                try
                {
                    await _gateway.OnUiAsync(() => _gateway.Context.Workspace.UpdateTodo(new UpdateTodoRequest
                    {
                        PaperId = newPaperId,
                        TodoId = todoId,
                        Order = order
                    }));
                }
                catch (PaperTodoPluginException ex)
                {
                    result.Warnings.Add($"待办顺序未完全还原：{ex.Message}");
                    break;
                }
            }
        }

        if (settings.RestoreLinks)
        {
            var linksDisabled = false;
            foreach (var (original, match) in matched)
            {
                if (string.IsNullOrWhiteSpace(original.LinkedSourcePaperId))
                {
                    continue;
                }

                if (!idMap.TryGetValue(original.LinkedSourcePaperId, out var target) ||
                    string.Equals(target, newPaperId, StringComparison.Ordinal))
                {
                    result.Warnings.Add($"待办“{Shorten(original.Text)}”要关联的纸片不在导入文件中，关联已丢弃。");
                    continue;
                }

                try
                {
                    await _gateway.OnUiAsync(() => _gateway.Context.Workspace.UpdateTodo(new UpdateTodoRequest
                    {
                        PaperId = newPaperId,
                        TodoId = match.Id,
                        UpdateLinkedPaper = true,
                        LinkedPaperId = target
                    }));
                    result.RestoredLinks++;
                }
                catch (PaperTodoPluginException ex) when (ex.Code == "paper_links_disabled")
                {
                    if (!linksDisabled)
                    {
                        linksDisabled = true;
                        result.Warnings.Add("宿主已关闭“启用待办关联纸片”，所有关联未还原。");
                    }
                }
                catch (PaperTodoPluginException ex)
                {
                    result.Warnings.Add($"待办“{Shorten(original.Text)}”的关联纸片未还原：{ex.Message}");
                }
            }
        }

        if (settings.RestoreReminders)
        {
            var now = DateTimeOffset.Now;
            foreach (var (original, match) in matched)
            {
                if (original.Done || original.ReminderAt is not { } reminder || reminder <= now)
                {
                    continue;
                }

                try
                {
                    await _gateway.OnUiAsync(() => _gateway.Context.Workspace.SetTodoReminder(
                        new SetTodoReminderRequest
                        {
                            PaperId = newPaperId,
                            TodoId = match.Id,
                            ReminderAt = reminder
                        }));
                    result.RestoredReminders++;
                }
                catch (PaperTodoPluginException ex)
                {
                    result.Warnings.Add($"待办“{Shorten(original.Text)}”的提醒未还原：{ex.Message}");
                }
            }
        }

        // 关联文件/文件夹没有写入 API，按纸片汇总一次提示。
        var pathLinks = source.Todos.Count(t => !string.IsNullOrWhiteSpace(t.LinkedPath));
        if (pathLinks > 0)
        {
            result.DroppedPathLinkNotes.Add(
                $"纸片“{DisplayTitle(importPaper)}”中有 {pathLinks} 条待办关联的文件或文件夹，导入后未保留。");
        }
    }

    private static bool IsFatal(string code) => code is
        "session_closed" or
        "app_exiting" or
        "data_reload_in_progress" or
        "paper_limit";

    private static ImportFailure Failure(int index, ImportPaper paper, PaperTodoPluginException ex) =>
        new(index + 1, DisplayTitle(paper), paper.Source.Type, ex.Code, ex.Message);

    private static string DisplayTitle(ImportPaper paper) =>
        string.IsNullOrWhiteSpace(paper.Source.Title) ? "（无标题）" : paper.Source.Title;

    private static string Shorten(string text) =>
        text.Length <= 20 ? text : text[..20] + "…";
}
