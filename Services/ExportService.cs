using PaperTodo.Plugin;

namespace PaperTodo.Plugin.DataIo;

/// <summary>通过宿主逻辑数据 API 采集快照并组装导出文档。</summary>
internal sealed class ExportService
{
    private const string MarkdownProviderId = "builtin.markdown";

    private readonly HostGateway _gateway;

    public ExportService(HostGateway gateway)
    {
        _gateway = gateway;
    }

    /// <summary>
    /// 在 UI 线程读取宿主数据并组装文档。序列化与写盘由调用方放到后台线程。
    /// </summary>
    public async Task<ExportDocument> CaptureAsync(
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

        foreach (var paper in papers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!selected.Contains(paper.Id) ||
                string.Equals(paper.Id, currentPaperId, StringComparison.Ordinal))
            {
                continue;
            }

            document.Papers.Add(await MapPaperAsync(paper, todosByPaper, cancellationToken));
        }

        return document;
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
            if (string.Equals(paper.BodyProviderId, MarkdownProviderId, StringComparison.Ordinal) &&
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
