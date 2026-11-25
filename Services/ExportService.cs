using PaperTodo.Plugin;

namespace PaperTodo.Plugin.DataIo;

/// <summary>通过宿主逻辑数据 API 采集快照并组装导出文档。</summary>
internal sealed class ExportService
{
    /// <summary>一次导出最多内嵌的图片总字节；超出后剩余图片只保留正文引用并提示。</summary>
    internal const long MaxEmbeddedImageBytes = 512L * 1024 * 1024;

    private readonly HostGateway _gateway;

    public ExportService(HostGateway gateway)
    {
        _gateway = gateway;
    }

    /// <summary>
    /// 在 UI 线程读取宿主数据并组装文档。序列化与写盘由调用方放到后台线程。
    /// </summary>
    public async Task<ExportCaptureResult> CaptureAsync(
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

        var images = new ImageCaptureState();
        var canCaptureImages = CanCaptureNoteImages();
        foreach (var paper in papers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!selected.Contains(paper.Id) ||
                string.Equals(paper.Id, currentPaperId, StringComparison.Ordinal))
            {
                continue;
            }

            var exportPaper = await MapPaperAsync(paper, todosByPaper, cancellationToken);
            document.Papers.Add(exportPaper);

            if (canCaptureImages &&
                string.Equals(exportPaper.Type, "note", StringComparison.Ordinal) &&
                exportPaper.ContentAvailable == true)
            {
                await CapturePaperImagesAsync(
                    paper.Id,
                    exportPaper,
                    images,
                    cancellationToken);
            }
        }

        if (images.Images.Count > 0)
        {
            // 有图片清单时才写 v2；无图片仍保持 v1，旧插件可以继续读取。
            document.Images = images.Images
                .Select(ExportPackageService.ToManifest)
                .ToList();
            document.SchemaVersion = ExportDocument.CurrentSchemaVersion;
        }

        return new ExportCaptureResult(
            document,
            images.Images,
            images.FailedReads,
            images.Truncated);
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
            // 旧宿主没有图片读取能力；正文照常导出。
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
                state.Images.Add(new CapturedExportImage(
                    paper.SourceId,
                    image.ImageId,
                    ExportPackageService.BuildImageEntry(
                        paper.SourceId,
                        image.ImageId,
                        image.Mime),
                    image.Mime,
                    image.Bytes,
                    image.Bytes.LongLength <= IPaperNoteAssetsApi.MaximumImageAppendBytes));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is PaperTodoPluginException or InvalidOperationException)
            {
                // 图片已被清理、归属变化或超出读取上限时保留引用并继续导出。
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

            exportPaper.Todos = todosByPaper.TryGetValue(paper.Id, out var todos)
                ? todos.Select(MapTodo).ToList()
                : [];
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
            }

            return exportPaper;
        }

        // 未知类型保守导出为空，导入时会因为类型非法被跳过。
        cancellationToken.ThrowIfCancellationRequested();
        return exportPaper;
    }

    private static ExportTodo MapTodo(TodoSnapshot todo) => new()
    {
        Text = todo.Text,
        Done = todo.Done,
        Order = todo.Order,
        ReminderAt = todo.ReminderAt,
        LinkedSourcePaperId = todo.LinkedPaperId,
        LinkedPath = todo.LinkedPath,
        LinkedPathIsDirectory = todo.LinkedPathIsDirectory
    };
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
