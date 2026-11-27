using PaperTodo.Plugin;

namespace PaperTodo.Plugin.DataIo;

/// <summary>通过 PaperTodo 逻辑数据 API 采集快照并组装导出文档。</summary>
internal sealed class ExportService
{
    /// <summary>
    /// 一次导出最多内嵌的图片总字节；超出后剩余图片只保留正文引用并提示。
    /// 必须小于导入端的 512 MiB 整包解压上限：正文与清单也会计入该上限，
    /// 否则合法的导出包可能被本插件自己的导入端拒绝，备份无法恢复。
    /// </summary>
    internal const long MaxEmbeddedImageBytes = 440L * 1024 * 1024;

    private readonly HostGateway _gateway;

    public ExportService(HostGateway gateway)
    {
        _gateway = gateway;
    }

    /// <summary>
    /// 在 UI 线程读取 PaperTodo 数据并组装文档（不含图片本体）。
    /// 调用方可以先用正文里的图片引用决定保存格式，再按需读取图片。
    /// </summary>
    public async Task<ExportDocument> CaptureDocumentAsync(
        IReadOnlyCollection<string> paperIds,
        string currentPaperId,
        CancellationToken cancellationToken)
    {
        if (!_gateway.Can(PaperTodoPermissionNames.PapersRead))
        {
            throw new PaperTodoPluginException("permission_denied", "缺少 papers.read 权限，无法读取纸片列表。");
        }

        var selected = new HashSet<string>(paperIds, StringComparer.Ordinal);
        var papers = await _gateway.OnUiAsync(() => _gateway.Context.Workspace.ListPapers());

        Dictionary<string, TodoSnapshot[]> todosByPaper = new(StringComparer.Ordinal);
        if (_gateway.Can(PaperTodoPermissionNames.TodosRead))
        {
            var allTodos = await _gateway.OnUiAsync(() => _gateway.Context.Workspace.ListTodos());
            todosByPaper = allTodos
                .GroupBy(t => t.PaperId, StringComparer.Ordinal)
                .ToDictionary(
                    g => g.Key,
                    g => g.OrderBy(t => t.Order).ToArray(),
                    StringComparer.Ordinal);
        }

        var document = new ExportDocument
        {
            ExportedAt = DateTimeOffset.Now,
            Source = new ExportSource
            {
                PluginId = "papertodo.dataio.native",
                PluginVersion = typeof(ExportService).Assembly.GetName().Version?.ToString(3) ?? "1.0.0",
                ApiVersion = _gateway.Context.ApiVersion,
                UiLanguage = _gateway.Context.UiLanguage,
                Note = "sourceId 仅供溯源与同文件内关联纸片还原；导入时会生成新 ID，不覆盖现有纸片。"
            }
        };

        var warnings = new List<string>();
        var skippedProviderBodies = 0;
        foreach (var paper in papers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!selected.Contains(paper.Id) ||
                string.Equals(paper.Id, currentPaperId, StringComparison.Ordinal))
            {
                continue;
            }

            var exportPaper = await MapPaperAsync(paper, todosByPaper, warnings, cancellationToken);
            if (string.Equals(exportPaper.Type, "note", StringComparison.Ordinal) &&
                exportPaper.ContentAvailable == false)
            {
                skippedProviderBodies++;
            }

            document.Papers.Add(exportPaper);
        }

        if (skippedProviderBodies > 0)
        {
            warnings.Insert(
                0,
                $"{skippedProviderBodies} 张笔记由第三方插件绘制正文，内容无法读取，" +
                "导出文件中没有它们的正文。");
        }

        document.Warnings = warnings.Count > 0 ? warnings : null;

        return document;
    }

    /// <summary>读取文档中笔记引用的图片本体；单张失败只跳过该图片。</summary>
    public async Task<ExportImageCapture> CaptureImagesAsync(
        ExportDocument document,
        CancellationToken cancellationToken)
    {
        if (!CanCaptureNoteImages())
        {
            return ExportImageCapture.Empty;
        }

        var state = new ImageCaptureState();
        foreach (var paper in document.Papers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!string.Equals(paper.Type, "note", StringComparison.Ordinal) ||
                paper.ContentAvailable != true)
            {
                continue;
            }

            await CapturePaperImagesAsync(
                paper.SourceId,
                paper,
                state,
                cancellationToken);
        }

        if (state.Images.Count > 0)
        {
            // 有图片清单时才写 v2；无图片仍保持 v1，旧插件可以继续读取。
            document.Images = state.Images
                .Select(ExportPackageService.ToManifest)
                .ToList();
            document.SchemaVersion = ExportDocument.CurrentSchemaVersion;
        }

        return new ExportImageCapture(
            state.Images,
            state.FailedReads,
            state.Truncated);
    }

    /// <summary>一次性采集正文与图片，供不需要提前决定保存格式的调用方使用。</summary>
    public async Task<ExportCaptureResult> CaptureAsync(
        IReadOnlyCollection<string> paperIds,
        string currentPaperId,
        CancellationToken cancellationToken)
    {
        var document = await CaptureDocumentAsync(paperIds, currentPaperId, cancellationToken);
        var images = await CaptureImagesAsync(document, cancellationToken);
        return new ExportCaptureResult(
            document,
            images.Images,
            images.FailedImageReads,
            images.ImageCaptureTruncated);
    }

    private bool CanCaptureNoteImages()
    {
        if (!_gateway.Can(PaperTodoPermissionNames.NotesRead))
        {
            return false;
        }

        try
        {
            _ = _gateway.Context.NoteAssets;
            return true;
        }
        catch (InvalidOperationException)
        {
            // 旧 PaperTodo 没有图片读取能力；正文照常导出。
            return false;
        }
    }

    /// <summary>逐张读取笔记引用到的图片。单张失败只跳过该图片，不影响整次导出。</summary>
    private async Task CapturePaperImagesAsync(
        string paperId,
        ExportPaper paper,
        ImageCaptureState state,
        CancellationToken cancellationToken)
    {
        var references = ImageReferenceScanner.Enumerate(paper.Content);
        if (references.Count == 0)
        {
            return;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reference in references)
        {
            if (!seen.Add(reference.ImageId))
            {
                continue;
            }

            if (state.Truncated)
            {
                break;
            }

            // 导入端只接受 2000 张图片；超出时必须截断，否则生成的包会被整体拒绝。
            if (state.Images.Count >= ImportPlanParser.MaxPackageImages)
            {
                state.Truncated = true;
                break;
            }

            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var image = await _gateway.OnUiAsync(
                    () => _gateway.Context.NoteAssets.ReadImage(paperId, reference.ImageId));
                if (state.TotalBytes + image.Bytes.LongLength > MaxEmbeddedImageBytes)
                {
                    state.Truncated = true;
                    break;
                }

                state.TotalBytes += image.Bytes.Length;

                // 导出端与导入端共用同一套“能否写回”规则：只按 8 MiB 体积判断会把
                // 需要压缩的 GIF/TIFF、超尺寸图片误报为可还原，备份看似完整却无法恢复。
                var restorable = ImageCompressionService.CanPotentiallyWriteBack(
                    image.Bytes,
                    image.Mime,
                    out var unrestorableReason);
                state.Images.Add(new CapturedExportImage(
                    paper.SourceId,
                    image.ImageId,
                    ExportPackageService.BuildImageEntry(
                        paper.SourceId,
                        image.ImageId,
                        image.Mime),
                    image.Mime,
                    image.Bytes,
                    restorable,
                    unrestorableReason));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                // 图片已被清理、归属变化、被杀毒/同步盘锁定或超出读取上限时，
                // 只跳过这一张图片，不能让整次导出失败。
                state.FailedReads++;
            }

            await HostGateway.YieldAsync();
        }
    }

    private sealed class ImageCaptureState
    {
        public List<CapturedExportImage> Images { get; } = [];
        public int FailedReads { get; set; }
        public bool Truncated { get; set; }
        public long TotalBytes { get; set; }
    }

    private async Task<ExportPaper> MapPaperAsync(
        PaperSnapshot paper,
        Dictionary<string, TodoSnapshot[]> todosByPaper,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var exportPaper = new ExportPaper
        {
            SourceId = paper.Id,
            Type = paper.Type,
            Title = paper.Title,
            Show = paper.IsVisible
        };

        if (string.Equals(paper.Type, "todo", StringComparison.Ordinal))
        {
            if (!_gateway.Can(PaperTodoPermissionNames.TodosRead))
            {
                throw new PaperTodoPluginException("permission_denied", "缺少 todos.read 权限，无法导出待办纸片。");
            }

            var todos = todosByPaper.TryGetValue(paper.Id, out var stored) ? stored : [];
            var truncated = false;
            if (todos.Length > ImportPlanParser.MaxTodosPerPaper)
            {
                // 导入端只接受单张纸片 200 条待办；导出端必须提前截断并明确告知，
                // 否则备份看似成功，恢复时整张纸片（含标题）会被丢弃。
                warnings.Add(
                    $"纸片“{DisplayTitle(paper.Title)}”有 {todos.Length} 条待办，" +
                    $"超过单张纸片 {ImportPlanParser.MaxTodosPerPaper} 条的可导入上限；" +
                    $"备份中只保留前 {ImportPlanParser.MaxTodosPerPaper} 条。");
                todos = todos.Take(ImportPlanParser.MaxTodosPerPaper).ToArray();
                truncated = true;
            }

            var mapped = new List<ExportTodo>(todos.Length);
            var overlong = 0;
            foreach (var todo in todos)
            {
                var text = todo.Text ?? "";
                if (text.Length > ImportPlanParser.MaxTodoTextChars)
                {
                    text = TruncateChars(text, ImportPlanParser.MaxTodoTextChars);
                    overlong++;
                }

                mapped.Add(MapTodo(todo, text));
            }

            if (overlong > 0)
            {
                warnings.Add(
                    $"纸片“{DisplayTitle(paper.Title)}”有 {overlong} 条待办超过 " +
                    $"{ImportPlanParser.MaxTodoTextChars} 字符上限，备份中已截断。");
                truncated = true;
            }

            exportPaper.Todos = mapped;
            exportPaper.TodosTruncated = truncated;
            return exportPaper;
        }

        if (string.Equals(paper.Type, "note", StringComparison.Ordinal))
        {
            exportPaper.ContentAvailable = false;
            exportPaper.Content = "";

            // 只有内置 Markdown 正文可以通过逻辑 API 读取；其他插件正文由各自 provider 拥有。
            if (string.Equals(paper.BodyProviderId, PaperProviders.Markdown, StringComparison.Ordinal) &&
                _gateway.Can(PaperTodoPermissionNames.NotesRead))
            {
                var note = await _gateway.OnUiAsync(() => _gateway.Context.Workspace.GetNote(paper.Id));
                exportPaper.ContentAvailable = note?.ContentAvailable ?? false;
                exportPaper.Content = note?.Content ?? "";

                // 导入端会跳过超过 10 万字符的笔记；导出端绝不能输出“会被自己拒绝”的备份，
                // 也不能静默截断——标记后导入端会禁止用该文件更新原笔记。
                if (exportPaper.Content.Length > ImportPlanParser.MaxNoteChars)
                {
                    var original = exportPaper.Content.Length;
                    exportPaper.Content = TruncateChars(exportPaper.Content, ImportPlanParser.MaxNoteChars);
                    exportPaper.ContentTruncated = true;
                    warnings.Add(
                        $"笔记“{DisplayTitle(paper.Title)}”正文有 {original} 字符，超过 " +
                        $"{ImportPlanParser.MaxNoteChars} 字符上限，备份中只保留前 " +
                        $"{ImportPlanParser.MaxNoteChars} 字符；不能用它更新原笔记。");
                }
            }

            return exportPaper;
        }

        // 未知类型保守导出为空，导入时会因为类型非法被跳过。
        cancellationToken.ThrowIfCancellationRequested();
        return exportPaper;
    }

    private static string DisplayTitle(string? title) =>
        string.IsNullOrWhiteSpace(title) ? "（无标题）" : title;

    /// <summary>按字符数截断，且不切断代理对（emoji 等补充平面字符）。</summary>
    private static string TruncateChars(string value, int maxChars)
    {
        if (value.Length <= maxChars)
        {
            return value;
        }

        var cut = char.IsHighSurrogate(value[maxChars - 1]) ? maxChars - 1 : maxChars;
        return value[..cut];
    }

    private static ExportTodo MapTodo(TodoSnapshot todo, string text) => new()
    {
        Text = text,
        Done = todo.Done,
        Order = todo.Order,
        ReminderAt = todo.ReminderAt,
        LinkedSourcePaperId = todo.LinkedPaperId,
        LinkedPath = todo.LinkedPath,
        LinkedPathIsDirectory = todo.LinkedPathIsDirectory
    };
}

/// <summary>图片本体采集结果：已读取的图片、失败数量与是否因超限截断。</summary>
internal sealed record ExportImageCapture(
    IReadOnlyList<CapturedExportImage> Images,
    int FailedImageReads,
    bool ImageCaptureTruncated)
{
    public static ExportImageCapture Empty { get; } = new([], 0, false);

    public bool HasImages => Images.Count > 0;

    /// <summary>无法按 PaperTodo 规则写回、只能保留在压缩包里的图片数量。</summary>
    public int OversizedImages => Images.Count(image => !image.Restorable);

    /// <summary>无法写回的原因（去重），用于导出结果中的预检提示。</summary>
    public IReadOnlyList<string> UnrestorableReasons => Images
        .Where(image => !image.Restorable && image.UnrestorableReason.Length > 0)
        .Select(image => image.UnrestorableReason)
        .Distinct(StringComparer.Ordinal)
        .ToList();
}

/// <summary>一次导出的结果：正文文档、已读取的图片和图片统计。</summary>
internal sealed record ExportCaptureResult(
    ExportDocument Document,
    IReadOnlyList<CapturedExportImage> Images,
    int FailedImageReads,
    bool ImageCaptureTruncated)
{
    public bool HasImages => Images.Count > 0;

    /// <summary>超过插件追加上限、只能保留在压缩包里的图片数量。</summary>
    public int OversizedImages => Images.Count(image => !image.Restorable);
}
