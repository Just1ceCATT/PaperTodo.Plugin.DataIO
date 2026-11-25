using System.IO;
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

        // 纯更新不需要 papers.create；只有确实要新建纸片时才要求该权限并做数量预检。
        var newPaperCount = plan.Papers.Count(
            paper => string.IsNullOrWhiteSpace(paper.UpdateTargetId));
        if (newPaperCount > 0)
        {
            if (!_gateway.Can(PaperTodoPermissionNames.PapersCreate))
            {
                result.FatalError = "缺少 papers.create 权限，无法新建纸片；如需仅更新已导入纸片，请取消勾选新纸片。";
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

            if (existingCount + newPaperCount > MaxPapers)
            {
                result.FatalError =
                    $"当前已有 {existingCount} 张纸片，再新建 {newPaperCount} 张将超过 {MaxPapers} 张上限。";
                return result;
            }
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
                    var isTodoPaper = string.Equals(
                        importPaper.Source.Type,
                        "todo",
                        StringComparison.OrdinalIgnoreCase);
                    if (createdNow)
                    {
                        result.CreatedPapers++;
                        result.CreatedTodos += importPaper.Source.Todos?.Count ?? 0;
                        if (isTodoPaper)
                        {
                            result.CreatedTodoPapers++;
                        }
                    }
                    else
                    {
                        result.UpdatedPapers++;
                        result.UpdatedTodos += importPaper.Source.Todos?.Count ?? 0;
                        if (isTodoPaper)
                        {
                            result.UpdatedTodoPapers++;
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(importPaper.Source.SourceId))
                    {
                        idMap[importPaper.Source.SourceId] = localId;
                        result.SourceIdToPaperId[importPaper.Source.SourceId] = localId;
                    }

                    // 图片紧跟正文写入，尽量缩短“正文已更新但图片仍是旧引用”的窗口。
                    try
                    {
                        await RestoreImagesAsync(
                            importPaper,
                            localId,
                            createdNow,
                            result,
                            cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex) when (ex is PaperTodoPluginException or InvalidOperationException)
                    {
                        result.Warnings.Add(
                            $"纸片“{DisplayTitle(importPaper)}”的图片未能写回：{ex.Message}");
                    }
                }
                catch (PaperTodoPluginException ex) when (IsFatal(ex.Code))
                {
                    result.FailedPapers++;
                    result.Failures.Add(Failure(i, importPaper, ex));
                    result.StoppedEarly = true;
                    result.FatalError = $"导入被中止（{ex.Code}）：{ex.Message}";
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
                $"纸片“{DisplayTitle(importPaper)}”的标题与本地不一致，当前版本不支持重命名，已保留本地标题。");
        }

        if (type == "note")
        {
            return await UpdateNoteAsync(importPaper, targetId);
        }

        return await UpdateTodoPaperAsync(importPaper, targetId, settings, result, cancellationToken);
    }

    /// <summary>更新笔记纸片：整体替换正文，但绝不接受会把本地图片引用写坏的来源。</summary>
    private async Task<(string Id, bool Created)> UpdateNoteAsync(
        ImportPaper importPaper,
        string targetId)
    {
        var source = importPaper.Source;

        // 只信任明确标记为“正文可读”且确实带有正文的笔记；否则不能用来清空本地正文。
        if (source.ContentAvailable != true || source.Content is null)
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
        if (current is not { ContentAvailable: true })
        {
            // 读不到本地正文时不贸然覆盖，避免在未知状态下清空笔记。
            throw new PaperTodoPluginException(
                "local_unavailable",
                "无法读取本地笔记正文，已跳过更新。");
        }

        if (MarkdownNormalize.ContentEquivalent(current.Content, source.Content))
        {
            // 正文等价（忽略行尾空行与末尾分隔线）时不写回，
            // 避免 Markdown 往返产生的规范化差异被持久化。
            return (targetId, false);
        }

        // 确认期间本地内容被修改过：放弃覆盖，让用户重新导入并确认。
        if (!FingerprintMatches(
                importPaper.UpdateTargetFingerprint,
                ImportIdentityService.ComputeNoteFingerprint(current.Content)))
        {
            throw new PaperTodoPluginException(
                "local_changed",
                "本地笔记在确认后已被修改，已跳过更新，请重新导入后确认。");
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
        DataIoSettings settings,
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

        // 确认期间本地待办被修改过：放弃覆盖/删除，让用户重新导入并确认。
        if (!FingerprintMatches(
                importPaper.UpdateTargetFingerprint,
                ImportIdentityService.ComputeTodoFingerprint(existing)))
        {
            throw new PaperTodoPluginException(
                "local_changed",
                "本地待办在确认后已被修改，已跳过更新，请重新导入后确认。");
        }

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
            if (!settings.DeleteMissingTodos)
            {
                // 用户关闭了“更新时删除文件中不存在的待办”：只合并新增与修改，绝不动本地数据。
                result.Warnings.Add(
                    $"纸片“{DisplayTitle(importPaper)}”中有 {removed.Count} 条本地待办不在文件中，" +
                    "已按设置保留（“更新时删除文件中不存在的待办”已关闭）。");
            }
            else if (_gateway.Can(PaperTodoPermissionNames.TodosDelete))
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

    /// <summary>没有记录指纹（旧映射/回退副本）时无法比对，视为通过。</summary>
    internal static bool FingerprintMatches(string? expected, string current) =>
        string.IsNullOrEmpty(expected) ||
        string.Equals(expected, current, StringComparison.Ordinal);

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
            Content = type == "note" ? BuildCreateContent(importPaper) : null,
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

                result.Warnings.Add($"标题“{normalized}”超过标题上限，已截断为“{truncated}”。");
                request = request with { Title = truncated.Length == 0 ? null : truncated };
            }
        }
    }

    /// <summary>
    /// 新建带图片的笔记时先移除旧图片引用，等图片写入成功后由最终替换恢复完整正文。
    /// 这样不会先以其他机器上失效的引用创建纸片，也避免末尾开放代码块阻止追加。
    /// </summary>
    private static string BuildCreateContent(ImportPaper importPaper)
    {
        var content = importPaper.Source.Content ?? "";
        if (importPaper.Images.Count == 0 || importPaper.ResolvedImageReferences == 0)
        {
            return content;
        }

        return importPaper.UsesRelativeImageLinks
            ? ImageReferenceScanner.RewriteRelativeImageLinks(content, _ => "")
            : ImageReferenceScanner.RewriteReferences(content, _ => "");
    }

    /// <summary>
    /// 把压缩包内的图片写回笔记：先逐张追加拿到本机图片 ID，再整体替换正文。
    /// 单张追加失败只保留原引用；整体写入失败时会尝试清理本次追加的尾部引用。
    /// </summary>
    private async Task RestoreImagesAsync(
        ImportPaper importPaper,
        string paperId,
        bool createdNow,
        ImportResult result,
        CancellationToken cancellationToken)
    {
        var images = importPaper.Images;
        if (images.Count == 0)
        {
            return;
        }

        if (!string.Equals(importPaper.Source.Type, "note", StringComparison.OrdinalIgnoreCase) ||
            importPaper.Source.ContentAvailable != true)
        {
            result.SkippedImages += images.Count;
            return;
        }

        if (importPaper.ResolvedImageReferences == 0)
        {
            // 正文没有引用这些图片，写回只会产生孤儿图片。
            result.SkippedImages += images.Count;
            return;
        }

        if (!_gateway.Can(PaperTodoPermissionNames.NotesAppend) ||
            !_gateway.Can(PaperTodoPermissionNames.NotesReplace))
        {
            result.SkippedImages += images.Count;
            result.Warnings.Add(
                $"纸片“{DisplayTitle(importPaper)}”包含 {images.Count} 张图片，" +
                "但缺少 notes.append/notes.replace 权限，图片未写回。");
            return;
        }

        try
        {
            _ = _gateway.Context.NoteAssets;
        }
        catch (InvalidOperationException)
        {
            result.SkippedImages += images.Count;
            result.Warnings.Add("当前宿主不支持插件写入笔记图片，压缩包中的图片未写回。");
            return;
        }

        var canReadImages = _gateway.Can(PaperTodoPermissionNames.NotesRead);
        var newIds = new Dictionary<string, string>(StringComparer.Ordinal);
        var idsByFile = new Dictionary<string, string>(StringComparer.Ordinal);
        var appended = new List<string>();
        var failed = 0;
        var skipped = 0;
        string? firstFailure = null;

        try
        {
            for (var index = 0; index < images.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var image = images[index];
                if (!image.Restorable)
                {
                    skipped++;
                    continue;
                }

                // 同一工作区重新导入时，图片往往已经存在且内容一致，直接复用原 ID，
                // 避免重复写入和正文图片 ID 每次导入都变化。
                if (canReadImages &&
                    !string.IsNullOrWhiteSpace(image.SourceImageId) &&
                    await CanReuseLocalImageAsync(paperId, image))
                {
                    newIds[image.SourceImageId] = image.SourceImageId;
                    idsByFile[image.File] = image.SourceImageId;
                    continue;
                }

                var imageName = Path.GetFileName(image.File);
                if (imageName.Length > 200)
                {
                    imageName = imageName[..200];
                }

                try
                {
                    var inserted = await _gateway.OnUiAsync(
                        () => _gateway.Context.NoteAssets.AppendImage(
                            paperId,
                            image.Bytes,
                            string.IsNullOrEmpty(imageName) ? null : imageName));
                    newIds[image.SourceImageId] = inserted.ImageId;
                    idsByFile[image.File] = inserted.ImageId;
                    appended.Add(inserted.MarkdownReference);
                }
                catch (PaperTodoPluginException ex) when (ex.Code == "image_reference_not_renderable")
                {
                    // 正文末尾处于未闭合代码块，继续追加同样会失败。
                    failed += images.Count - index;
                    result.Warnings.Add(
                        $"纸片“{DisplayTitle(importPaper)}”的正文末尾有未闭合的代码块，图片无法写回；" +
                        "请先修正正文后重新导入。");
                    break;
                }
                catch (Exception ex) when (ex is PaperTodoPluginException or InvalidOperationException)
                {
                    failed++;
                    firstFailure ??= ex.Message;
                }

                await HostGateway.YieldAsync();
            }
        }
        catch (OperationCanceledException)
        {
            await TryRollbackAppendedImagesAsync(paperId, appended, result);
            throw;
        }

        result.FailedImages += failed;
        result.SkippedImages += skipped;

        if (newIds.Count == 0)
        {
            if (failed > 0)
            {
                var reason = string.IsNullOrWhiteSpace(firstFailure) ? "" : $"（{firstFailure}）";
                result.Warnings.Add(
                    $"纸片“{DisplayTitle(importPaper)}”的 {failed} 张图片未能写回" +
                    $"{reason}，相关引用保持原样。");
            }

            // 新建时为了追加图片移除过引用；图片全部失败时把文件中的原始引用写回，保留位置。
            if (createdNow &&
                !string.Equals(
                    BuildCreateContent(importPaper),
                    importPaper.Source.Content ?? "",
                    StringComparison.Ordinal))
            {
                try
                {
                    await _gateway.OnUiAsync(() => _gateway.Context.Workspace.WriteNote(
                        new WriteNoteRequest
                        {
                            PaperId = paperId,
                            Content = importPaper.Source.Content ?? "",
                            Mode = NoteWriteMode.Replace
                        }));
                }
                catch (Exception ex) when (ex is PaperTodoPluginException or InvalidOperationException)
                {
                    result.Warnings.Add(
                        $"纸片“{DisplayTitle(importPaper)}”的原始图片引用未能写回：{ex.Message}");
                }
            }

            return;
        }

        var finalContent = BuildContentWithNewImageIds(importPaper, newIds, idsByFile);
        try
        {
            // 全部图片都能复用且正文没有变化时，不触发无意义的保存。
            var current = await TryGetNoteAsync(paperId);
            if (current is { ContentAvailable: true } &&
                string.Equals(current.Content, finalContent, StringComparison.Ordinal))
            {
                result.RestoredImages += newIds.Count;
            }
            else
            {
                await _gateway.OnUiAsync(() => _gateway.Context.Workspace.WriteNote(new WriteNoteRequest
                {
                    PaperId = paperId,
                    Content = finalContent,
                    Mode = NoteWriteMode.Replace
                }));
                result.RestoredImages += newIds.Count;
            }
        }
        catch (OperationCanceledException)
        {
            await TryRollbackAppendedImagesAsync(paperId, appended, result);
            throw;
        }
        catch (Exception ex) when (ex is PaperTodoPluginException or InvalidOperationException)
        {
            await TryRollbackAppendedImagesAsync(paperId, appended, result);
            result.FailedImages += newIds.Count;
            result.Warnings.Add(
                $"纸片“{DisplayTitle(importPaper)}”的图片已写入，但正文更新失败：" +
                $"{ex.Message}；已尝试清理追加的图片引用。");
            return;
        }

        if (failed > 0)
        {
            var reason = string.IsNullOrWhiteSpace(firstFailure) ? "" : $"（{firstFailure}）";
            result.Warnings.Add(
                $"纸片“{DisplayTitle(importPaper)}”有 {failed} 张图片未能写回" +
                $"{reason}，相关引用保持原样。");
        }

        if (skipped > 0)
        {
            result.Warnings.Add(
                $"纸片“{DisplayTitle(importPaper)}”有 {skipped} 张图片超过 8 MiB，仅保留在压缩包中。");
        }
    }

    /// <summary>本地已有同一张笔记的同 ID 图片且字节完全一致时，可直接复用该 ID。</summary>
    private async Task<bool> CanReuseLocalImageAsync(string paperId, ImportImageAsset image)
    {
        try
        {
            var existing = await _gateway.OnUiAsync(
                () => _gateway.Context.NoteAssets.ReadImage(paperId, image.SourceImageId));
            return existing.Bytes.AsSpan().SequenceEqual(image.Bytes);
        }
        catch (Exception ex) when (ex is PaperTodoPluginException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>把正文里的旧图片引用替换成本机新写入的图片 ID。</summary>
    internal static string BuildContentWithNewImageIds(
        ImportPaper importPaper,
        IReadOnlyDictionary<string, string> newIds,
        IReadOnlyDictionary<string, string> idsByFile)
    {
        var content = importPaper.Source.Content ?? "";
        return importPaper.UsesRelativeImageLinks
            ? ImageReferenceScanner.RewriteRelativeImageLinks(
                content,
                file => idsByFile.TryGetValue(file, out var newId) ? $"i:{newId}" : null)
            : ImageReferenceScanner.RewriteReferences(
                content,
                imageId => newIds.TryGetValue(imageId, out var newId) ? $"i:{newId}" : null);
    }

    /// <summary>
    /// 正文替换失败时，尝试把本次追加到末尾的图片引用移除。
    /// 用户在此期间改动过正文时放弃回滚，避免覆盖新内容。
    /// </summary>
    private async Task TryRollbackAppendedImagesAsync(
        string paperId,
        IReadOnlyList<string> references,
        ImportResult result)
    {
        if (references.Count == 0)
        {
            return;
        }

        try
        {
            var note = await TryGetNoteAsync(paperId);
            if (note is not { ContentAvailable: true })
            {
                return;
            }

            var content = note.Content ?? "";
            for (var index = references.Count - 1; index >= 0; index--)
            {
                var reference = references[index];
                if (content.EndsWith("\r\n" + reference, StringComparison.Ordinal))
                {
                    content = content[..^(2 + reference.Length)];
                }
                else if (content.EndsWith("\n" + reference, StringComparison.Ordinal))
                {
                    content = content[..^(1 + reference.Length)];
                }
                else if (string.Equals(content, reference, StringComparison.Ordinal))
                {
                    content = "";
                }
                else
                {
                    result.Warnings.Add(
                        "图片恢复失败后未能清理已追加的引用：笔记正文在此期间被修改，已保留原样。");
                    return;
                }
            }

            await _gateway.OnUiAsync(() => _gateway.Context.Workspace.WriteNote(new WriteNoteRequest
            {
                PaperId = paperId,
                Content = content,
                Mode = NoteWriteMode.Replace
            }));
        }
        catch (Exception ex) when (ex is PaperTodoPluginException or InvalidOperationException)
        {
            result.Warnings.Add("图片恢复失败后未能清理已追加的图片引用，笔记末尾可能多出图片。");
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
                result.Warnings.Add($"待办“{Shorten(original.Text)}”未能找回，可能已被自动清除。");
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
                        result.Warnings.Add("已关闭“启用待办关联纸片”，所有关联未还原。");
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
