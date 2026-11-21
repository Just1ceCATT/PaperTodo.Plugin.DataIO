using PaperTodo.Plugin;

namespace PaperTodo.Plugin.DataIo;

/// <summary>
/// 执行导入：第一遍逐纸片创建，第二遍还原顺序、纸片链接与提醒。
/// 第一遍不把链接和提醒放进初始待办，避免宿主校验失败导致整张纸片创建失败。
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

        if (existingCount + plan.Papers.Count > MaxPapers)
        {
            result.FatalError =
                $"当前已有 {existingCount} 张纸片，再导入 {plan.Papers.Count} 张将超过宿主 {MaxPapers} 张上限。";
            return result;
        }

        var idMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var created = new List<(ImportPaper Paper, string NewId)>();

        // 第一遍：逐张创建纸片。
        for (var i = 0; i < plan.Papers.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var importPaper = plan.Papers[i];
            _progress?.Report($"正在导入 {i + 1}/{plan.Papers.Count}：{DisplayTitle(importPaper)}");

            try
            {
                var newId = await CreateOneAsync(importPaper, settings, result);
                created.Add((importPaper, newId));
                result.CreatedPapers++;
                result.CreatedTodos += importPaper.Source.Todos?.Count ?? 0;

                if (!string.IsNullOrWhiteSpace(importPaper.Source.SourceId))
                {
                    idMap[importPaper.Source.SourceId] = newId;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
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

        // 第二遍：补顺序、链接、提醒。
        if (!result.StoppedEarly)
        {
            for (var i = 0; i < created.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (paper, newId) = created[i];
                _progress?.Report($"正在还原 {i + 1}/{created.Count}：{DisplayTitle(paper)}");
                await RepairAsync(paper, newId, settings, idMap, result, cancellationToken);
                await HostGateway.YieldAsync();
            }
        }

        return result;
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
            result.Warnings.Add("缺少 todos.update 权限，已跳过顺序、链接和提醒还原。");
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
            result.Warnings.Add($"读取新纸片待办失败，未还原顺序/链接/提醒：{ex.Message}");
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
                    result.Warnings.Add($"待办“{Shorten(original.Text)}”的链接目标不在导入文件中，链接已丢弃。");
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
                        result.Warnings.Add("宿主已关闭待办-纸片链接，所有链接未还原。");
                    }
                }
                catch (PaperTodoPluginException ex)
                {
                    result.Warnings.Add($"待办“{Shorten(original.Text)}”的链接未还原：{ex.Message}");
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

        // 路径链接没有写入 API，按纸片汇总一次提示。
        var pathLinks = source.Todos.Count(t => !string.IsNullOrWhiteSpace(t.LinkedPath));
        if (pathLinks > 0)
        {
            result.DroppedPathLinkNotes.Add(
                $"纸片“{DisplayTitle(importPaper)}”中有 {pathLinks} 条文件/文件夹路径链接无法还原。");
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
