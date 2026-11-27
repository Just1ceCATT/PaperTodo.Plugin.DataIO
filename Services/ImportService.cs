using System.IO;
using System.Text;
using PaperTodo.Plugin;

namespace PaperTodo.Plugin.DataIo;

/// <summary>
/// 执行导入：第一遍逐纸片创建，第二遍还原顺序、待办关联纸片与提醒。
/// 第一遍不把关联纸片和提醒放进初始待办，避免 PaperTodo 校验失败导致整张纸片创建失败。
/// </summary>
internal sealed class ImportService
{
    private const int MaxPapers = ImportPlanParser.MaxPapers;

    private readonly HostGateway _gateway;
    private readonly IProgress<string>? _progress;

    /// <summary>折叠失败（如 PaperTodo 未开启胶囊模式）后，本次导入不再对后续纸片重试折叠。</summary>
    private bool _capsuleCollapseUnavailable;

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
            paper => paper.SkipReason is null && string.IsNullOrWhiteSpace(paper.UpdateTargetId));
        if (newPaperCount > 0)
        {
            if (!_gateway.Can(PaperTodoPermissionNames.PapersCreate))
            {
                result.FatalError = "缺少 papers.create 权限，无法新建纸片；如需仅更新已导入纸片，请取消勾选新纸片。";
                result.StoppedEarly = true;
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
                result.FatalError = $"读取现有纸片失败：{HostErrorText.Describe(ex)}";
                result.StoppedEarly = true;
                return result;
            }

            if (existingCount + newPaperCount > MaxPapers)
            {
                result.FatalError =
                    $"当前已有 {existingCount} 张纸片，再新建 {newPaperCount} 张将超过 {MaxPapers} 张上限。";
                result.StoppedEarly = true;
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
                if (importPaper.SkipReason is { } skipReason)
                {
                    // 解析阶段已判定无法导入（第三方正文、正文缺失等）：绝不能再创建空纸片。
                    result.SkippedTitles.Add($"{DisplayTitle(importPaper)}（{skipReason}）");
                    continue;
                }

                _progress?.Report($"正在导入 {i + 1}/{plan.Papers.Count}：{DisplayTitle(importPaper)}");

                try
                {
                    var createdNow = string.IsNullOrWhiteSpace(importPaper.UpdateTargetId);
                    var imagesHandled = false;
                    string localId;
                    if (createdNow)
                    {
                        localId = await CreateOneAsync(importPaper, settings, result);
                    }
                    else
                    {
                        var update = await UpdateOneAsync(
                            importPaper,
                            importPaper.UpdateTargetId!,
                            settings,
                            result,
                            cancellationToken);
                        localId = update.Id;
                        createdNow = update.Created;
                        imagesHandled = update.ImagesHandled;
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

                    // 更新笔记的图片已由 UpdateNoteAsync 按“更新语义”处理完（失败时也回退为
                    // 移除坏引用的正文），这里不能再用“新建语义”重跑，否则会把刚移除的坏引用
                    // 写回、重复追加图片并重复累计统计；只有新建的纸片才需要在这里补图片。
                    try
                    {
                        if (!imagesHandled && createdNow)
                        {
                            await RestoreImagesAsync(
                                importPaper,
                                localId,
                                createdNow: true,
                                result,
                                cancellationToken);
                        }
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
                    result.FatalError = $"导入被中止：{HostErrorText.Describe(ex)}";
                    break;
                }
                catch (PaperTodoPluginException ex)
                {
                    result.FailedPapers++;
                    result.Failures.Add(Failure(i, importPaper, ex));
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
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
                    var (paper, localId, createdNow) = processed[i];
                    _progress?.Report($"正在还原 {i + 1}/{processed.Count}：{DisplayTitle(paper)}");
                    try
                    {
                        await RepairAsync(
                            paper,
                            localId,
                            createdNow,
                            settings,
                            plan.SupportsPaperLinks,
                            plan.SupportsReminders,
                            idMap,
                            plan.FileSourceIds,
                            result,
                            cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        // 第一遍已经写入纸片；第二遍的 PaperTodo 异常不能冲出 ImportAsync，
                        // 否则调用方拿不到 ImportResult，用户只看到“操作失败”却已有半写入数据。
                        result.Warnings.Add(
                            $"纸片“{DisplayTitle(paper)}”的顺序/关联/提醒还原失败：{ex.Message}");
                    }

                    await HostGateway.YieldAsync();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // PaperTodo 没有事务回滚能力：已完成的部分已经写入，必须如实标记为“部分完成”，
            // 由调用方提示用户，而不是让取消看起来像什么都没发生。
            result.Cancelled = true;
        }

        return result;
    }

    /// <summary>
    /// 更新身份映射命中的本地纸片：笔记整体替换，待办按文本做增删改。
    /// 标题、类型等 PaperTodo 不支持的字段保持不变，并在标题不一致时提示。
    /// 所有可预见的失败（权限、内容不可用、破坏性差异）都在写入前判定，避免部分生效。
    /// </summary>
    private async Task<(string Id, bool Created, bool ImagesHandled)> UpdateOneAsync(
        ImportPaper importPaper,
        string targetId,
        DataIoSettings settings,
        ImportResult result,
        CancellationToken cancellationToken)
    {
        var source = importPaper.Source;
        var type = (source.Type ?? "").Trim().ToLowerInvariant();

        // 截断的文件不是权威数据：允许新建，但绝不能用来覆盖本地纸片。
        if (importPaper.UpdateBlockedReason is { } blocked)
        {
            throw new PaperTodoPluginException(
                "truncated_source",
                $"{blocked}，已跳过该纸片的更新；请选择“新增”或使用完整的导出文件。");
        }

        var target = await _gateway.OnUiAsync(
            () => _gateway.Context.Workspace.GetPaper(targetId));
        if (target == null ||
            !string.Equals(target.Type, type, StringComparison.Ordinal))
        {
            // 用户已明确选择“更新”，目标却在确认后被删除或改变了类型：
            // 静默改为新建会产生用户没有确认过的数据，必须中止本次导入。
            throw new PaperTodoPluginException(
                "target_changed",
                $"纸片“{DisplayTitle(importPaper)}”的更新目标在确认后已被删除或类型改变，" +
                "已中止本次导入。请重新导入文件并再次确认。");
        }

        if (!string.IsNullOrWhiteSpace(importPaper.SafeTitle) &&
            !string.Equals(target.Title, importPaper.SafeTitle, StringComparison.Ordinal))
        {
            result.Warnings.Add(
                $"纸片“{DisplayTitle(importPaper)}”的标题与本地不一致，当前版本不支持重命名，已保留本地标题。");
        }

        if (type == "note")
        {
            return await UpdateNoteAsync(
                importPaper,
                targetId,
                result,
                cancellationToken);
        }

        return await UpdateTodoPaperAsync(importPaper, targetId, settings, result, cancellationToken);
    }

    /// <summary>更新笔记纸片：整体替换正文，但绝不接受会把本地图片引用写坏的来源。</summary>
    private async Task<(string Id, bool Created, bool ImagesHandled)> UpdateNoteAsync(
        ImportPaper importPaper,
        string targetId,
        ImportResult result,
        CancellationToken cancellationToken)
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

        // 正文逐字等价时通常可以直接返回，避免把 Markdown 往返的规范化差异持久化。
        // 但文件声明了无法写回的图片资产时，更新需要移除坏引用，
        // 不能把“正文相同”当成“无事可做”，否则这些引用永远不会被清理；
        // 同理，有随包图片时也要走图片写回/复用流程。
        if (importPaper.Images.Count == 0 &&
            !HasUnrestorableDeclaredReferences(importPaper) &&
            MarkdownNormalize.ContentEquivalent(current.Content, source.Content))
        {
            return (targetId, false, false);
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

        // 图片失败不能连带丢弃文字：无法写回的引用会被移除，其余正文仍然更新。
        string content;
        if (importPaper.Images.Count > 0)
        {
            var imagesHandled = await RestoreImagesAsync(
                importPaper,
                targetId,
                createdNow: false,
                result,
                cancellationToken,
                abortOnUpdate: true,
                expectedLocalContent: current.Content);
            if (imagesHandled)
            {
                await HostGateway.YieldAsync();
                return (targetId, false, true);
            }

            // 图片完全没能写回（权限变化、 PaperTodo 不支持等）：只移除清单中确认无法写回的引用；
            // 用户手写链接以及本机可能仍然有效的引用保持原样。
            (content, var removed) = StripUnrestorableReferences(importPaper, source.Content ?? "");
            if (removed > 0)
            {
                result.Warnings.Add(
                    $"纸片“{DisplayTitle(importPaper)}”的 {removed} 处图片引用无法写回，" +
                    "已移除这些引用并更新其余正文。");
            }
        }
        else
        {
            // 没有随包图片时（例如普通 JSON），只移除已知无法还原的相对链接；
            // 同一工作区的 i: 引用保持原样。
            (content, var removed) = StripUnrestorableReferences(importPaper, source.Content ?? "");
            if (removed > 0)
            {
                var reason = GetImageRestoreBlocker(importPaper) ?? "图片无法写回";
                result.Warnings.Add(
                    $"纸片“{DisplayTitle(importPaper)}”的 {removed} 处图片引用无法写回" +
                    $"（{reason}），已移除这些引用并更新其余正文。");
            }
        }

        // 没有任何引用需要移除时不必重写正文（例如正文等价、图片全部失败但清单为空）。
        if (string.Equals(content, current.Content, StringComparison.Ordinal))
        {
            return (targetId, false, false);
        }

        // 图片写回期间用户可能仍在编辑；兜底写入前重新读取并复核指纹，
        // 避免用文件正文覆盖写回窗口内的本地修改。
        var latest = await TryGetNoteAsync(targetId);
        if (latest is not { ContentAvailable: true })
        {
            throw new PaperTodoPluginException(
                "local_unavailable",
                "无法读取本地笔记正文，已跳过更新。");
        }

        if (!FingerprintMatches(
                importPaper.UpdateTargetFingerprint,
                ImportIdentityService.ComputeNoteFingerprint(latest.Content)))
        {
            throw new PaperTodoPluginException(
                "local_changed",
                "本地笔记在图片写回期间被修改，已跳过更新，请重新导入后确认。");
        }

        await _gateway.OnUiAsync(() => _gateway.Context.Workspace.WriteNote(new WriteNoteRequest
        {
            PaperId = targetId,
            Content = content,
            Mode = NoteWriteMode.Replace
        }));
        await HostGateway.YieldAsync();
        return (targetId, false, false);
    }

    /// <summary>正文是否引用了无法写回的图片；更新需要移除这些引用。</summary>
    internal static bool HasUnrestorableDeclaredReferences(ImportPaper paper)
    {
        var content = paper.Source.Content ?? "";
        if (paper.UsesRelativeImageLinks)
        {
            // Markdown 压缩包内残留的 i: 引用来自导出时读取失败的图片，本机无法保证指向同一张图。
            if (ImageReferenceScanner.Enumerate(content).Count > 0)
            {
                return true;
            }

            return paper.UnavailableImageFiles.Count > 0 &&
                   ImageReferenceScanner.EnumerateRelativeImageLinks(content)
                       .Any(paper.UnavailableImageFiles.Contains);
        }

        if (!paper.HasImageManifest &&
            paper.Images.Count == 0 &&
            paper.UnavailableImageIds.Count == 0)
        {
            // 普通 JSON 没有图片清单：正文里的 i: 引用在同一工作区通常仍然有效，不能擅自移除。
            return false;
        }

        // 有图片清单时，正文中的 i: 引用全部来自来源机器：
        // 不在清单里说明导出时就没有读到该图片，其 ID 在本机可能正好命中另一张图，必须移除。
        var restorableIds = paper.Images
            .Where(image => image.Restorable)
            .Select(image => image.SourceImageId)
            .ToHashSet(StringComparer.Ordinal);
        return ImageReferenceScanner.Enumerate(content)
            .Any(reference => !restorableIds.Contains(reference.ImageId));
    }

    /// <summary>移除没有可用资产、确定无法写回的图片引用；保留其余正文。</summary>
    private static (string Content, int Removed) StripUnrestorableReferences(
        ImportPaper paper,
        string content)
    {
        if (paper.UsesRelativeImageLinks)
        {
            // 清单登记过但校验失败的图片也算“清单里的引用”：它确实无法写回，必须移除。
            var manifestFiles = paper.Images
                .Select(image => image.File)
                .Concat(paper.UnavailableImageFiles)
                .ToHashSet(StringComparer.Ordinal);
            var restorableFiles = paper.Images
                .Where(image => image.Restorable)
                .Select(image => image.File)
                .ToHashSet(StringComparer.Ordinal);

            // 用户手写的 images/... 链接不在清单中，必须原样保留；
            // 但 Markdown 压缩包里的 i: 引用是导出时读取失败的图片，本机 ID 可能指向别的图，必须移除。
            return ImageReferenceScanner.RemoveUnresolvedReferences(
                content,
                url => !url.StartsWith("i:", StringComparison.Ordinal) &&
                       (!manifestFiles.Contains(url) || restorableFiles.Contains(url)));
        }

        // 普通 JSON（没有图片清单）：引用可能在本机仍然有效，不擅自删除。
        // 但压缩包声明过图片、只是全部校验失败时，仍要按清单移除坏引用。
        if (!paper.HasImageManifest && paper.Images.Count == 0)
        {
            return (content, 0);
        }

        // 有清单时正文里的 i: 引用全部来自来源机器：清单未登记说明导出时未包含该图，
        // 其 ID 在本机可能正好命中另一张图片（图片 ID 是工作区级连续编号），必须移除。
        var restorableIds = paper.Images
            .Where(image => image.Restorable)
            .Select(image => image.SourceImageId)
            .ToHashSet(StringComparer.Ordinal);
        return ImageReferenceScanner.RemoveUnresolvedReferences(
            content,
            url => !url.StartsWith("i:", StringComparison.Ordinal) ||
                   restorableIds.Contains(url[2..]));
    }

    /// <summary>把外部清单里的文件名清洗成 Windows 可用的图片名，并按字符边界截断。</summary>
    private static string SanitizeImageFileName(string file)
    {
        var name = Path.GetFileName(file);
        var builder = new StringBuilder(name.Length);
        foreach (var character in name)
        {
            var invalid = character < ' ' ||
                          character == '' ||
                          character is '*' or '?' or '"' or '<' or '>' or '|';
            builder.Append(invalid ? '_' : character);
        }

        var sanitized = builder.ToString().Trim().TrimEnd('.');
        if (sanitized.Length == 0)
        {
            return "image";
        }

        const int maxNameLength = 200;
        if (sanitized.Length > maxNameLength)
        {
            // 避免按 char 截断时把代理对切成两半。
            var cut = maxNameLength;
            if (char.IsHighSurrogate(sanitized[cut - 1]))
            {
                cut--;
            }

            sanitized = sanitized[..cut];
        }

        return sanitized;
    }

    /// <summary>
    /// 判断当前正文是否只比基线多了本次追加的图片引用。
    /// PaperTodo 追加引用时可能补充换行，因此逐条从尾部剥离后按正文等价比较。
    /// </summary>
    private static bool HasOnlyOurAppendedChanges(
        string? current,
        string baseline,
        IReadOnlyList<string> appended)
    {
        if (current is null)
        {
            return false;
        }

        var remainder = current;
        for (var index = appended.Count - 1; index >= 0; index--)
        {
            var reference = appended[index].Trim();
            if (reference.Length == 0)
            {
                continue;
            }

            var position = remainder.LastIndexOf(reference, StringComparison.Ordinal);
            if (position < 0 ||
                !string.IsNullOrWhiteSpace(remainder[(position + reference.Length)..]))
            {
                return false;
            }

            remainder = remainder[..position];
        }

        return MarkdownNormalize.ContentEquivalent(remainder, baseline);
    }

    /// <summary>更新待办纸片：先完整匹配并校验权限，再统一写入，避免“改一半才报失败”。</summary>
    private async Task<(string Id, bool Created, bool ImagesHandled)> UpdateTodoPaperAsync(
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
        return (targetId, false, false);
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

        // 胶囊模式：先以隐藏方式创建，创建后折叠再按需显示，避免导入的纸片直接展开铺满桌面。
        var shouldShow = settings.ImportShowCreated || source.Show;
        var presentation = _gateway.Context.Workspace as IPaperWorkspacePresentationApi;
        var useCapsule = settings.CollapseImportedPapers && presentation is not null && !_capsuleCollapseUnavailable;

        var request = new CreatePaperRequest
        {
            Type = type,
            Title = importPaper.SafeTitle,
            Show = useCapsule ? false : shouldShow,
            Content = type == "note" ? BuildCreateContent(importPaper) : null,
            Todos = todos
        };

        string paperId;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var mutated = await _gateway.OnUiAsync(() => _gateway.Context.Workspace.CreatePaper(request));
                paperId = mutated.PaperId;
                break;
            }
            catch (PaperTodoPluginException ex) when (
                attempt < 2 &&
                ex.Code == "invalid_params" &&
                TitleSanitizer.TryParseTitleLimit(ex.Message, out var limit))
            {
                // PaperTodo 标题上限可按用户设置变化，从错误文案解析后截断重试。
                var normalized = TitleSanitizer.Normalize(request.Title);
                var truncated = TitleSanitizer.TruncateByDisplayWidth(
                    normalized,
                    Math.Clamp(limit, 1, TitleSanitizer.MaxConfiguredTitleLength) * 2);

                result.Warnings.Add($"标题“{normalized}”超过标题上限，已截断为“{truncated}”。");
                request = request with { Title = truncated.Length == 0 ? null : truncated };
            }
        }

        if (useCapsule)
        {
            await ApplyCreatedCapsuleAsync(presentation!, paperId, shouldShow, result);
        }

        return paperId;
    }

    /// <summary>
    /// 把刚创建的纸片折叠成胶囊，再按需显示，避免它直接展开在桌面上。
    /// PaperTodo 未开启胶囊模式时无法折叠，退回普通显示，并让后续纸片不再重试。
    /// </summary>
    private async Task ApplyCreatedCapsuleAsync(
        IPaperWorkspacePresentationApi presentation,
        string paperId,
        bool shouldShow,
        ImportResult result)
    {
        try
        {
            await _gateway.OnUiAsync(() => presentation.CollapsePaper(paperId));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _capsuleCollapseUnavailable = true;
            result.Warnings.Add(
                $"未能把新纸片折叠为胶囊（{ex.Message}）。请确认 PaperTodo 已开启胶囊模式；" +
                "本次其余纸片将按普通方式显示。");
            if (shouldShow)
            {
                await TryShowCreatedAsync(presentation, paperId, result);
            }

            return;
        }

        if (shouldShow)
        {
            await TryShowCreatedAsync(presentation, paperId, result);
        }
    }

    private async Task TryShowCreatedAsync(
        IPaperWorkspacePresentationApi presentation,
        string paperId,
        ImportResult result)
    {
        try
        {
            await _gateway.OnUiAsync(() => presentation.ShowPaper(paperId, activate: false));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            result.Warnings.Add($"纸片已导入，但未能自动显示：{ex.Message}");
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
    private async Task<bool> RestoreImagesAsync(
        ImportPaper importPaper,
        string paperId,
        bool createdNow,
        ImportResult result,
        CancellationToken cancellationToken,
        bool abortOnUpdate = false,
        string? expectedLocalContent = null)
    {
        // 只写回正文实际引用的图片，避免为清单里未被引用的图片留下孤儿资源。
        var images = FilterReferencedImages(importPaper);
        if (images.Count == 0)
        {
            return false;
        }

        if (!string.Equals(importPaper.Source.Type, "note", StringComparison.OrdinalIgnoreCase) ||
            importPaper.Source.ContentAvailable != true)
        {
            result.SkippedImages += images.Count;
            return false;
        }

        if (!_gateway.Can(PaperTodoPermissionNames.NotesAppend) ||
            !_gateway.Can(PaperTodoPermissionNames.NotesReplace))
        {
            result.SkippedImages += images.Count;
            result.Warnings.Add(
                $"纸片“{DisplayTitle(importPaper)}”包含 {images.Count} 张图片，" +
                "但缺少 notes.append/notes.replace 权限，图片未写回。");
            return false;
        }

        try
        {
            _ = _gateway.Context.NoteAssets;
        }
        catch (InvalidOperationException)
        {
            result.SkippedImages += images.Count;
            result.Warnings.Add("当前 PaperTodo 不支持插件写入笔记图片，压缩包中的图片未写回。");
            return false;
        }

        var canReadImages = _gateway.Can(PaperTodoPermissionNames.NotesRead);

        // 更新时按本地正文里的图片逐张比对字节，复用内容一致的本机图片。
        // 不能用来源图片 ID 直接读取：首次导入时 PaperTodo 会分配新的图片 ID，
        // 来源 ID 在本机通常不存在，按 ID 复用永远失败并导致每次导入都追加副本。
        var reusableLocalImages = canReadImages && expectedLocalContent is not null
            ? await ReadLocalImageBytesAsync(paperId, expectedLocalContent)
            : null;
        var usedLocalImageIds = new HashSet<string>(StringComparer.Ordinal);

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

                // 同一工作区重新导入时，本地正文里的图片往往与文件内容一致，
                // 直接复用本机图片 ID，避免重复写入和正文图片 ID 每次导入都变化。
                // 必须在“不可写回”判断之前尝试：一张 3000 像素的 GIF 虽然无法重新压缩，
                // 但本机正文引用的很可能就是同一张图，跳过复用会把本来有效的引用删掉。
                // 逐字节比对在后台线程执行：多张大图时这类比较会长时间占用 UI 线程。
                var reusableId = reusableLocalImages is null
                    ? null
                    : await Task.Run(
                        () => TakeReusableImageId(
                            reusableLocalImages,
                            usedLocalImageIds,
                            image.Bytes),
                        cancellationToken);
                if (reusableId is not null)
                {
                    newIds[image.SourceImageId] = reusableId;
                    idsByFile[image.File] = reusableId;
                    continue;
                }

                if (!image.Restorable)
                {
                    skipped++;
                    continue;
                }

                var imageName = SanitizeImageFileName(image.File);

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
                    // PaperTodo 拒绝在未闭合代码块后追加图片；沿用 PaperTodo 文案，继续保留正文文本。
                    failed += images.Count - index;
                    firstFailure ??= ex.Message;
                    break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
                {
                    // 单张图片的 IO/权限/参数异常只影响这一张，不能中断整张纸片的其余图片。
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

        if (newIds.Count == 0 && !abortOnUpdate)
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

            return false;
        }

        var finalContent = BuildContentWithNewImageIds(importPaper, newIds, idsByFile);
        if (abortOnUpdate)
        {
            // 更新时图片失败不能连带丢掉文字：移除无法写回的引用，其余正文照常写入。
            // 但只删除“清单里确实登记过的引用”；用户手写的其它 images/ 链接必须保留。
            var mappedIds = newIds.Values.ToHashSet(StringComparer.Ordinal);
            var resolvedFiles = idsByFile.Keys.ToHashSet(StringComparer.Ordinal);
            var manifestFiles = importPaper.Images
                .Select(image => image.File)
                .Concat(importPaper.UnavailableImageFiles)
                .ToHashSet(StringComparer.Ordinal);
            (finalContent, _) = ImageReferenceScanner.RemoveUnresolvedReferences(
                finalContent,
                url => url.StartsWith("images/", StringComparison.Ordinal)
                    ? // 用户手写的相对链接不在清单中，保留；清单内的只保留成功写回的。
                      !manifestFiles.Contains(url) || resolvedFiles.Contains(url)
                    : // 正文此时已用新图片 ID 改写：仍不是本机新 ID 的 i: 引用无法写回，必须移除，
                      // 否则会在本机命中另一张同号图片或显示破图。
                      !url.StartsWith("i:", StringComparison.Ordinal) ||
                      mappedIds.Contains(url[2..]));
        }

        try
        {
            var current = await TryGetNoteAsync(paperId);

            // 图片写回期间用户可能在编辑器里继续修改目标笔记；
            // 追加图片本身会改变正文（尾部多出引用），因此按“基线 + 本次追加的引用”识别，
            // 只要出现其它改动就放弃覆盖。
            if (abortOnUpdate &&
                current is { ContentAvailable: true } &&
                expectedLocalContent is not null &&
                !HasOnlyOurAppendedChanges(current.Content, expectedLocalContent, appended))
            {
                await TryRollbackAppendedImagesAsync(paperId, appended, result);
                throw new PaperTodoPluginException(
                    "local_changed",
                    "本地笔记在图片写回期间被修改，已跳过更新，请重新导入后确认。");
            }

            // 全部图片都能复用且正文没有变化时，不触发无意义的保存。
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
        catch (PaperTodoPluginException ex) when (ex.Code == "local_changed")
        {
            // 本地在确认后被修改：这是安全中止，必须让调用方把原因报给用户。
            await TryRollbackAppendedImagesAsync(paperId, appended, result);
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            await TryRollbackAppendedImagesAsync(paperId, appended, result);
            result.FailedImages += newIds.Count;
            result.Warnings.Add(
                $"纸片“{DisplayTitle(importPaper)}”的图片已写入，但正文更新失败：" +
                $"{ex.Message}；已尝试清理追加的图片引用。");

            // 新建纸片当初为了追加图片先移除了正文引用；正文写回失败时必须把
            // 文件中的原始引用写回，避免永久残留 ![]() 空引用。
            if (!abortOnUpdate && createdNow &&
                !string.Equals(
                    BuildCreateContent(importPaper),
                    importPaper.Source.Content ?? "",
                    StringComparison.Ordinal))
            {
                try
                {
                    await _gateway.OnUiAsync(() => _gateway.Context.Workspace.WriteNote(new WriteNoteRequest
                    {
                        PaperId = paperId,
                        Content = importPaper.Source.Content ?? "",
                        Mode = NoteWriteMode.Replace
                    }));
                }
                catch (Exception restoreEx) when (restoreEx is not OutOfMemoryException)
                {
                    result.Warnings.Add(
                        $"纸片“{DisplayTitle(importPaper)}”的原始图片引用也未能写回：{restoreEx.Message}");
                }
            }

            return false;
        }

        if (failed > 0)
        {
            var reason = string.IsNullOrWhiteSpace(firstFailure) ? "" : $"（{firstFailure}）";
            var suffix = abortOnUpdate
                ? "已移除对应引用并更新其余正文。"
                : "相关引用保持原样。";
            result.Warnings.Add(
                $"纸片“{DisplayTitle(importPaper)}”有 {failed} 张图片未能写回" +
                $"{reason}，{suffix}");
        }

        if (skipped > 0)
        {
            result.Warnings.Add(abortOnUpdate
                ? $"纸片“{DisplayTitle(importPaper)}”有 {skipped} 张图片无法按 PaperTodo 规则压缩到可写回的大小，已移除对应引用并更新其余正文。"
                : $"纸片“{DisplayTitle(importPaper)}”有 {skipped} 张图片无法按 PaperTodo 规则压缩到可写回的大小，仅保留在压缩包中。");
        }

        return true;
    }

    /// <summary>只保留正文实际引用的图片，未引用的清单条目不应写入图片库。</summary>
    private static List<ImportImageAsset> FilterReferencedImages(ImportPaper paper)
    {
        var content = paper.Source.Content ?? "";
        if (paper.UsesRelativeImageLinks)
        {
            var referenced = ImageReferenceScanner.EnumerateRelativeImageLinks(content)
                .ToHashSet(StringComparer.Ordinal);
            return paper.Images
                .Where(image => referenced.Contains(image.File))
                .ToList();
        }

        var ids = ImageReferenceScanner.Enumerate(content)
            .Select(reference => reference.ImageId)
            .ToHashSet(StringComparer.Ordinal);
        return paper.Images
            .Where(image => ids.Contains(image.SourceImageId))
            .ToList();
    }

    /// <summary>
    /// 更新笔记前判断正文图片是否能全部安全写回；返回非空字符串表示阻止原因。
    /// Markdown 包残留的 i: 引用来自导出时读取失败，无法保证在本机指向同一张图。
    /// </summary>
    internal static string? GetImageRestoreBlocker(ImportPaper paper)
    {
        var content = paper.Source.Content ?? "";
        if (paper.UsesRelativeImageLinks)
        {
            if (ImageReferenceScanner.Enumerate(content).Count > 0)
            {
                return "压缩包未包含部分图片";
            }

            var byFile = new Dictionary<string, ImportImageAsset>(StringComparer.Ordinal);
            foreach (var image in paper.Images)
            {
                byFile.TryAdd(image.File, image);
            }

            foreach (var link in ImageReferenceScanner.EnumerateRelativeImageLinks(content))
            {
                if (!byFile.TryGetValue(link, out var image))
                {
                    return paper.UnavailableImageFiles.Contains(link)
                        ? "图片文件缺失或校验失败"
                        : "压缩包缺少正文引用的图片";
                }

                if (!image.Restorable)
                {
                    return "有图片无法按 PaperTodo 规则压缩到可写回的大小";
                }
            }

            return null;
        }

        // 普通 JSON（没有图片清单）：保留原 i: 引用，同一工作区通常仍然有效。
        // 压缩包声明过图片时即使全部校验失败，也要继续检查引用。
        if (!paper.HasImageManifest && paper.Images.Count == 0 && paper.UnavailableImageIds.Count == 0)
        {
            return null;
        }

        var byId = new Dictionary<string, ImportImageAsset>(StringComparer.Ordinal);
        foreach (var image in paper.Images)
        {
            byId.TryAdd(image.SourceImageId, image);
        }

        foreach (var reference in ImageReferenceScanner.Enumerate(content))
        {
            if (!byId.TryGetValue(reference.ImageId, out var image))
            {
                return paper.UnavailableImageIds.Contains(reference.ImageId)
                    ? "图片文件缺失或校验失败"
                    : "压缩包缺少正文引用的图片";
            }

            if (!image.Restorable)
            {
                return "有图片无法压缩到 8 MiB 以内";
            }
        }

        return null;
    }

    /// <summary>
    /// 读取本地正文引用的图片字节，供复用比对。数量或总大小超过阈值时放弃复用，
    /// 避免为一次导入把整篇笔记的图片全部驻留内存。
    /// </summary>
    private async Task<Dictionary<string, byte[]>?> ReadLocalImageBytesAsync(
        string paperId,
        string content)
    {
        const int maxImages = 40;
        const long maxTotalBytes = 128L * 1024 * 1024;

        var references = ImageReferenceScanner.Enumerate(content);
        if (references.Count == 0)
        {
            return new Dictionary<string, byte[]>(StringComparer.Ordinal);
        }

        // 按去重后的图片数判断上限：同一张图在正文里出现多次不应挤占额度。
        var map = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        long totalBytes = 0;
        foreach (var reference in references)
        {
            if (map.ContainsKey(reference.ImageId))
            {
                continue;
            }

            if (map.Count >= maxImages)
            {
                break;
            }

            try
            {
                var image = await _gateway.OnUiAsync(
                    () => _gateway.Context.NoteAssets.ReadImage(paperId, reference.ImageId));
                totalBytes += image.Bytes.LongLength;

                // 超过总量阈值后停止继续读取，但已读到的图片仍然参与复用：
                // 整体放弃复用会让每张图都重新追加，正是需要避免的膨胀。
                if (totalBytes > maxTotalBytes)
                {
                    break;
                }

                map[reference.ImageId] = image.Bytes;
            }
            catch (Exception ex) when (ex is PaperTodoPluginException or InvalidOperationException)
            {
                // 单张读取失败只影响该张的复用，不影响其它图片写回。
            }
        }

        return map;
    }

    /// <summary>供后台线程调用的复用查找：命中时返回本地图片 ID。</summary>
    private static string? TakeReusableImageId(
        Dictionary<string, byte[]> localImages,
        HashSet<string> usedLocalImageIds,
        byte[] bytes) =>
        TryTakeReusableImageId(localImages, usedLocalImageIds, bytes, out var imageId)
            ? imageId
            : null;

    /// <summary>在本地正文图片中找一张内容完全一致、且尚未被复用的图片。</summary>
    private static bool TryTakeReusableImageId(
        Dictionary<string, byte[]> localImages,
        HashSet<string> usedLocalImageIds,
        byte[] bytes,
        out string imageId)
    {
        foreach (var (id, localBytes) in localImages)
        {
            if (!usedLocalImageIds.Contains(id) &&
                localBytes.AsSpan().SequenceEqual(bytes))
            {
                usedLocalImageIds.Add(id);
                imageId = id;
                return true;
            }
        }

        imageId = "";
        return false;
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
        bool createdNow,
        DataIoSettings settings,
        bool supportsPaperLinks,
        bool supportsReminders,
        Dictionary<string, string> idMap,
        IReadOnlySet<string> fileSourceIds,
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

        // 按 (文本, 完成状态) 建立队列，避免 PaperTodo 自动清除/置底后按索引错配。
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

        if (settings.RestoreLinks && supportsPaperLinks)
        {
            var linksDisabled = false;
            var cleared = 0;
            foreach (var (original, match) in matched)
            {
                if (string.IsNullOrWhiteSpace(original.LinkedSourcePaperId))
                {
                    // 源文件没有关联：更新时同步清除本地多出的“关联纸片”。
                    // 关联路径没有写入 API，无法还原，因此带路径的本地项保持不动，避免误删。
                    if (createdNow || !CanClearLinkedPaper(match))
                    {
                        continue;
                    }

                    try
                    {
                        await ClearLinkedPaperAsync(newPaperId, match.Id);
                        cleared++;
                    }
                    catch (PaperTodoPluginException ex) when (ex.Code == "paper_links_disabled")
                    {
                        if (!linksDisabled)
                        {
                            linksDisabled = true;
                            result.Warnings.Add("已关闭“启用待办关联纸片”，关联变更未生效。");
                        }

                        break;
                    }
                    catch (PaperTodoPluginException ex)
                    {
                        result.Warnings.Add($"待办“{Shorten(original.Text)}”的关联纸片未能清除：{ex.Message}");
                    }

                    continue;
                }

                if (original.LinkedTargetOutsideFile)
                {
                    // 目标没有被包含进本次导出文件（部分导出或第三方正文纸片）：
                    // 文件不是完整快照，不能据此清除本地仍然有效的关联。
                    if (!createdNow)
                    {
                        result.Warnings.Add(
                            $"待办“{Shorten(original.Text)}”的关联目标不在导出文件中，本地关联保持不变。");
                    }

                    continue;
                }

                var hasTarget = idMap.TryGetValue(original.LinkedSourcePaperId, out var target);
                var selfLink = hasTarget &&
                               string.Equals(target, newPaperId, StringComparison.Ordinal);
                if (!hasTarget || selfLink)
                {
                    if (!selfLink &&
                        fileSourceIds.Contains(original.LinkedSourcePaperId))
                    {
                        // 目标纸片在文件里存在、只是本轮没有导入（未勾选、内容无变化被跳过或导入失败）：
                        // 本地关联仍然有效，不能按“文件中已不存在”清除。
                        result.Warnings.Add(
                            $"待办“{Shorten(original.Text)}”要关联的纸片本轮未导入，本地关联保持不变。");
                        continue;
                    }

                    result.Warnings.Add(selfLink
                        ? $"待办“{Shorten(original.Text)}”要关联的纸片是它自身，关联已丢弃。"
                        : $"待办“{Shorten(original.Text)}”要关联的纸片不在导入文件中，关联已丢弃。");

                    // 自关联没有意义；目标不在文件内时则同步清除本地旧关联，保持与文件一致。
                    if (!createdNow && CanClearLinkedPaper(match))
                    {
                        try
                        {
                            await ClearLinkedPaperAsync(newPaperId, match.Id);
                            cleared++;
                        }
                        catch (PaperTodoPluginException ex) when (ex.Code == "paper_links_disabled")
                        {
                            if (!linksDisabled)
                            {
                                linksDisabled = true;
                                result.Warnings.Add("已关闭“启用待办关联纸片”，关联变更未生效。");
                            }

                            break;
                        }
                        catch (PaperTodoPluginException ex)
                        {
                            result.Warnings.Add($"待办“{Shorten(original.Text)}”的关联纸片未能清除：{ex.Message}");
                        }
                    }

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

            if (cleared > 0)
            {
                result.Warnings.Add(
                    $"纸片“{DisplayTitle(importPaper)}”有 {cleared} 条待办的关联纸片在文件中不存在，已清除。");
            }
        }

        if (settings.RestoreReminders && supportsReminders)
        {
            var now = DateTimeOffset.Now;
            var cleared = 0;
            foreach (var (original, match) in matched)
            {
                if (original.Done)
                {
                    continue;
                }

                if (original.ReminderAt is { } reminder)
                {
                    if (reminder <= now)
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
                    catch (PaperTodoPluginException ex) when (ex.Code == "reminders_disabled")
                    {
                        result.Warnings.Add("提醒功能未开启，提醒未还原。");
                        break;
                    }
                    catch (PaperTodoPluginException ex)
                    {
                        result.Warnings.Add($"待办“{Shorten(original.Text)}”的提醒未还原：{ex.Message}");
                    }

                    continue;
                }

                // 源文件没有提醒：更新时同步清除本地多出的提醒。
                if (createdNow || match.ReminderAt is null || match.Done)
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
                            ReminderAt = null
                        }));
                    cleared++;
                }
                catch (PaperTodoPluginException ex) when (ex.Code == "reminders_disabled")
                {
                    result.Warnings.Add("提醒功能未开启，本地提醒未能清除。");
                    break;
                }
                catch (PaperTodoPluginException ex)
                {
                    result.Warnings.Add($"待办“{Shorten(original.Text)}”的提醒未能清除：{ex.Message}");
                }
            }

            if (cleared > 0)
            {
                result.Warnings.Add(
                    $"纸片“{DisplayTitle(importPaper)}”有 {cleared} 条待办的提醒在文件中不存在，已清除。");
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

    /// <summary>本地是“关联纸片”而不是关联路径时，才能安全清除（清除会同时清掉路径）。</summary>
    private static bool CanClearLinkedPaper(TodoSnapshot todo) =>
        !string.IsNullOrWhiteSpace(todo.LinkedPaperId) &&
        string.IsNullOrWhiteSpace(todo.LinkedPath);

    private Task ClearLinkedPaperAsync(string paperId, string todoId) =>
        _gateway.OnUiAsync(() => _gateway.Context.Workspace.UpdateTodo(new UpdateTodoRequest
        {
            PaperId = paperId,
            TodoId = todoId,
            UpdateLinkedPaper = true,
            LinkedPaperId = null
        }));

    private static bool IsFatal(string code) => code is
        "session_closed" or
        "app_exiting" or
        "data_reload_in_progress" or
        "paper_limit" or
        "target_changed";

    private static ImportFailure Failure(int index, ImportPaper paper, PaperTodoPluginException ex) =>
        new(index + 1, DisplayTitle(paper), paper.Source.Type, ex.Code, HostErrorText.Describe(ex));

    private static string DisplayTitle(ImportPaper paper) =>
        string.IsNullOrWhiteSpace(paper.Source.Title) ? "（无标题）" : paper.Source.Title;

    private static string Shorten(string text) =>
        text.Length <= 20 ? text : text[..20] + "…";
}
