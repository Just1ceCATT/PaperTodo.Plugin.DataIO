using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using PaperTodo.Plugin;

namespace PaperTodo.Plugin.DataIo;

/// <summary>
/// 工具纸片会话：负责生命周期、事件订阅与导入导出编排。
/// 所有宿主数据调用都经 HostGateway 切回 UI 线程。
/// </summary>
internal sealed class DataIoSession : IPaperBodySession
{
    private const string MarkdownProviderId = "builtin.markdown";

    private readonly PaperBodyContext _context;
    private readonly DataIoView _view;
    private readonly HostGateway _gateway;
    private readonly ExportService _exportService;
    private readonly DispatcherTimer _debounce;
    private readonly IDisposable? _subscription;

    private DataIoState _state;
    private DataIoSettings _settings;
    private List<PaperSnapshot> _papers = [];
    private Dictionary<string, TodoSnapshot[]> _todosByPaper = new(StringComparer.Ordinal);
    private CancellationTokenSource? _operation;
    private bool _busy;
    private bool _disposed;

    public DataIoSession(PaperBodyContext context)
    {
        _context = context;
        _state = DataIoState.Parse(context.StateJson);
        _settings = DataIoSettings.Parse(context.SettingsJson);

        _view = new DataIoView();
        _gateway = new HostGateway(context, _view.Dispatcher);
        _exportService = new ExportService(_gateway);

        _view.ExportRequested += OnExportRequested;
        _view.ImportRequested += OnImportRequested;
        _view.FilterChanged += OnFilterChanged;

        _view.SetFilter(_state.Filter, _state.Search);
        _view.ApplyTheme(context.Theme, context.Body.Controls);

        _debounce = new DispatcherTimer(DispatcherPriority.Background, _view.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(150)
        };
        _debounce.Tick += OnDebounceTick;

        _subscription = TrySubscribe();
        _view.Dispatcher.InvokeAsync(() => _ = RefreshAsync(), DispatcherPriority.Loaded);
    }

    public FrameworkElement View => _view;

    public void Commit() => SaveState();

    public void RefreshFromModel() => ScheduleRefresh();

    public void CancelInteractions() => _operation?.Cancel();

    public void OnActivated() => ScheduleRefresh();

    public void OnDeactivated()
    {
    }

    public void OnVisibilityChanged(bool visible)
    {
        if (visible)
        {
            ScheduleRefresh();
        }
    }

    public void OnPresentationChanged(bool visible)
    {
    }

    public void OnThemeChanged(PaperBodyTheme theme) =>
        _view.ApplyTheme(theme, _context.Body.Controls);

    public void OnTypographyChanged(PaperBodyTheme theme) =>
        _view.ApplyTheme(theme, _context.Body.Controls);

    public void OnDpiChanged()
    {
    }

    public bool OnRuntimeMessage(JsonElement message) => false;

    public void OnSettingsChanged(string settingsJson)
    {
        _settings = DataIoSettings.Parse(settingsJson);
        ScheduleRefresh();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _operation?.Cancel();
        _debounce.Stop();
        _debounce.Tick -= OnDebounceTick;
        _subscription?.Dispose();

        _view.ExportRequested -= OnExportRequested;
        _view.ImportRequested -= OnImportRequested;
        _view.FilterChanged -= OnFilterChanged;
    }

    // ---------- 事件与刷新 ----------

    private void OnExportRequested(object? sender, EventArgs e) =>
        _ = RunGuardedAsync(ExportAsync);

    private void OnImportRequested(object? sender, EventArgs e) =>
        _ = RunGuardedAsync(ImportAsync);

    private void OnFilterChanged(object? sender, EventArgs e)
    {
        _state.Filter = _view.CurrentFilter;
        _state.Search = _view.CurrentSearch;
        SaveState();
    }

    private void OnDebounceTick(object? sender, EventArgs e)
    {
        _debounce.Stop();
        _ = RefreshAsync();
    }

    private void ScheduleRefresh()
    {
        if (_disposed)
        {
            return;
        }

        if (!_view.Dispatcher.CheckAccess())
        {
            _view.Dispatcher.BeginInvoke(new Action(ScheduleRefresh), DispatcherPriority.Background);
            return;
        }

        _debounce.Stop();
        _debounce.Start();
    }

    private async Task RefreshAsync(bool force = false)
    {
        if (_disposed || (_busy && !force))
        {
            return;
        }

        try
        {
            if (!_gateway.Can(PaperTodoPermissionNames.PapersRead))
            {
                _view.SetAvailability(false, _gateway.Can(PaperTodoPermissionNames.PapersCreate));
                SetStatus("缺少 papers.read 权限，无法读取纸片列表。请在插件设置中重新授权后重启 PaperTodo。");
                return;
            }

            IReadOnlyList<PaperSnapshot> papers;
            try
            {
                papers = await _gateway.OnUiAsync(() => _context.Workspace.ListPapers());
            }
            catch (PaperTodoPluginException ex)
            {
                _view.SetAvailability(false, _gateway.Can(PaperTodoPermissionNames.PapersCreate));
                SetStatus(DescribeError(ex));
                return;
            }

            var todos = Array.Empty<TodoSnapshot>();
            if (_gateway.Can(PaperTodoPermissionNames.TodosRead))
            {
                try
                {
                    todos = (await _gateway.OnUiAsync(() => _context.Workspace.ListTodos())).ToArray();
                }
                catch (PaperTodoPluginException)
                {
                    todos = Array.Empty<TodoSnapshot>();
                }
            }

            _papers = papers.ToList();
            _todosByPaper = todos
                .GroupBy(t => t.PaperId, StringComparer.Ordinal)
                .ToDictionary(
                    g => g.Key,
                    g => g.OrderBy(t => t.Order).ToArray(),
                    StringComparer.Ordinal);

            _view.SetRows(BuildRows());
            _view.SetAvailability(
                _gateway.Can(PaperTodoPermissionNames.PapersRead),
                _gateway.Can(PaperTodoPermissionNames.PapersCreate));

            if (!_busy)
            {
                SetStatus($"共 {_papers.Count} 张纸片。勾选后导出，或导入 JSON 文件。");
            }
        }
        catch (Exception ex)
        {
            SetStatus($"刷新列表失败：{ex.Message}");
        }
    }

    private IReadOnlyList<PaperRowItem> BuildRows()
    {
        var canReadTodos = _gateway.Can(PaperTodoPermissionNames.TodosRead);
        var canReadNotes = _gateway.Can(PaperTodoPermissionNames.NotesRead);
        var rows = new List<PaperRowItem>();

        foreach (var paper in _papers)
        {
            if (string.Equals(paper.Id, _context.PaperId, StringComparison.Ordinal))
            {
                continue;
            }

            var isPluginProvider =
                !string.Equals(paper.BodyProviderId, MarkdownProviderId, StringComparison.Ordinal);

            if (string.Equals(paper.Type, "todo", StringComparison.Ordinal))
            {
                if (!_settings.IncludeTodoPapers)
                {
                    continue;
                }

                var todos = _todosByPaper.TryGetValue(paper.Id, out var values)
                    ? values
                    : Array.Empty<TodoSnapshot>();
                var done = todos.Count(t => t.Done);

                rows.Add(new PaperRowItem
                {
                    PaperId = paper.Id,
                    Title = string.IsNullOrWhiteSpace(paper.Title) ? "（无标题）" : paper.Title,
                    Type = "todo",
                    TypeLabel = "待办",
                    Summary = todos.Length == 0
                        ? "暂无待办"
                        : $"{todos.Length} 项待办 · {done} 项已完成",
                    Warning = canReadTodos ? "" : "缺少 todos.read 权限，无法导出待办",
                    CanSelect = canReadTodos
                });
            }
            else if (string.Equals(paper.Type, "note", StringComparison.Ordinal))
            {
                if (isPluginProvider)
                {
                    if (!_settings.IncludePluginPapers)
                    {
                        continue;
                    }

                    rows.Add(new PaperRowItem
                    {
                        PaperId = paper.Id,
                        Title = string.IsNullOrWhiteSpace(paper.Title) ? "（无标题）" : paper.Title,
                        Type = "note",
                        TypeLabel = "笔记",
                        Summary = "其他插件纸片",
                        Warning = "正文由其他插件提供，导入后为空笔记",
                        CanSelect = canReadNotes
                    });
                }
                else
                {
                    if (!_settings.IncludeNotePapers)
                    {
                        continue;
                    }

                    rows.Add(new PaperRowItem
                    {
                        PaperId = paper.Id,
                        Title = string.IsNullOrWhiteSpace(paper.Title) ? "（无标题）" : paper.Title,
                        Type = "note",
                        TypeLabel = "笔记",
                        Summary = "内置 Markdown 笔记",
                        Warning = canReadNotes ? "" : "缺少 notes.read 权限，无法导出正文",
                        CanSelect = canReadNotes
                    });
                }
            }
        }

        return rows;
    }

    // ---------- 导出 ----------

    private async Task ExportAsync()
    {
        if (!_gateway.Can(PaperTodoPermissionNames.PapersRead))
        {
            SetStatus("缺少 papers.read 权限，无法导出。");
            return;
        }

        var selected = _view.GetSelectedPaperIds();
        if (selected.Count == 0)
        {
            SetStatus("请先勾选要导出的纸片。");
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "导出纸片数据",
            Filter = "PaperTodo 数据 (*.json)|*.json|所有文件 (*.*)|*.*",
            DefaultExt = ".json",
            AddExtension = true,
            FileName = $"papertodo-export-{DateTime.Now:yyyyMMdd-HHmmss}.json"
        };

        var initial = ResolveDirectory(_state.LastExportDirectory);
        if (initial != null)
        {
            dialog.InitialDirectory = initial;
        }

        if (dialog.ShowDialog(Window.GetWindow(_view)) != true)
        {
            return;
        }

        _state.LastExportDirectory = Path.GetDirectoryName(dialog.FileName) ?? "";
        SaveState();

        SetStatus("正在读取数据…");
        var document = await _exportService.CaptureAsync(selected, _context.PaperId, CancellationToken.None);

        SetStatus("正在写入文件…");
        var json = await Task.Run(() => JsonSerializer.Serialize(document, ExportJson.Options));
        await Task.Run(() => JsonFiles.WriteAllTextAtomic(dialog.FileName, json));

        var unavailableNotes = document.Papers.Count(
            p => string.Equals(p.Type, "note", StringComparison.Ordinal) && p.ContentAvailable == false);
        var status = $"已导出 {document.Papers.Count} 张纸片：{dialog.FileName}";
        if (unavailableNotes > 0)
        {
            status += $"（{unavailableNotes} 张笔记正文不可读取，已按空正文导出）";
        }

        SetStatus(status);
    }

    // ---------- 导入 ----------

    private async Task ImportAsync()
    {
        if (!_gateway.Can(PaperTodoPermissionNames.PapersCreate))
        {
            SetStatus("缺少 papers.create 权限，无法导入。");
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "导入纸片数据",
            Filter = "PaperTodo 数据 (*.json)|*.json|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        var initial = ResolveDirectory(_state.LastImportDirectory);
        if (initial != null)
        {
            dialog.InitialDirectory = initial;
        }

        if (dialog.ShowDialog(Window.GetWindow(_view)) != true)
        {
            return;
        }

        _state.LastImportDirectory = Path.GetDirectoryName(dialog.FileName) ?? "";
        SaveState();

        SetStatus("正在读取导入文件…");
        ImportPlan plan;
        try
        {
            plan = await Task.Run(() => ImportPlanParser.ParseFile(dialog.FileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            SetStatus($"读取导入文件失败：{ex.Message}");
            return;
        }

        if (plan.FatalError != null)
        {
            ShowMessage(plan.FatalError, "无法导入", MessageBoxButton.OK, MessageBoxImage.Warning);
            SetStatus(plan.FatalError);
            return;
        }

        if (_settings.ConfirmBeforeImport)
        {
            var confirmation = BuildConfirmation(plan);
            if (ShowMessage(confirmation, "确认导入", MessageBoxButton.OKCancel, MessageBoxImage.Question) !=
                MessageBoxResult.OK)
            {
                SetStatus("已取消导入。");
                return;
            }
        }

        using var cts = new CancellationTokenSource();
        _operation = cts;
        string summary;
        try
        {
            var progress = new Progress<string>(SetStatus);
            var result = await new ImportService(_gateway, progress)
                .ImportAsync(plan, _settings, cts.Token);

            ShowImportResult(result);
            summary = result.FatalError ??
                $"导入完成：新增 {result.CreatedPapers} 张纸片，失败 {result.FailedPapers} 张。";
        }
        finally
        {
            _operation = null;
        }

        // 刷新会重写状态栏，因此在刷新后恢复本次导入摘要。
        await RefreshAsync(force: true);
        SetStatus(summary);
    }

    // ---------- 辅助 ----------

    private IDisposable? TrySubscribe()
    {
        // 按已授权种类裁剪事件，避免任一缺失导致 Subscribe 直接抛 permission_denied。
        var kinds = new HashSet<PaperTodoEventKind>();
        if (_gateway.Can(PaperTodoPermissionNames.PapersRead))
        {
            kinds.Add(PaperTodoEventKind.PaperCreated);
            kinds.Add(PaperTodoEventKind.PaperChanged);
            kinds.Add(PaperTodoEventKind.PaperDeleted);
        }

        if (_gateway.Can(PaperTodoPermissionNames.TodosRead))
        {
            kinds.Add(PaperTodoEventKind.TodoCreated);
            kinds.Add(PaperTodoEventKind.TodoChanged);
            kinds.Add(PaperTodoEventKind.TodoDeleted);
        }

        if (_gateway.Can(PaperTodoPermissionNames.NotesRead))
        {
            kinds.Add(PaperTodoEventKind.NoteChanged);
        }

        if (kinds.Count == 0)
        {
            return null;
        }

        try
        {
            return _context.Workspace.Subscribe(
                new PaperTodoEventFilter { Kinds = kinds, ExcludeOwnOperations = true },
                _ => ScheduleRefresh());
        }
        catch (PaperTodoPluginException)
        {
            return null;
        }
    }

    private async Task RunGuardedAsync(Func<Task> action)
    {
        if (_busy || _disposed)
        {
            return;
        }

        _busy = true;
        _view.SetBusy(true);
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
            SetStatus("操作已取消。");
        }
        catch (PaperTodoPluginException ex)
        {
            SetStatus($"宿主拒绝了操作：{ex.Code} · {ex.Message}");
        }
        catch (Exception ex)
        {
            SetStatus($"操作失败：{ex.Message}");
        }
        finally
        {
            _busy = false;
            _view.SetBusy(false);
            _operation = null;
        }
    }

    private void SaveState()
    {
        if (_disposed)
        {
            return;
        }

        _context.SaveStateJson(DataIoState.Serialize(_state));
    }

    private void SetStatus(string text)
    {
        if (!_disposed)
        {
            _view.SetStatus(text);
        }
    }

    private static string BuildConfirmation(ImportPlan plan)
    {
        var todoPapers = plan.Papers.Count(
            p => string.Equals(p.Source.Type, "todo", StringComparison.OrdinalIgnoreCase));
        var notePapers = plan.Papers.Count - todoPapers;

        var lines = new List<string>
        {
            $"将向当前工作区追加导入 {plan.Papers.Count} 张纸片：",
            $"- 待办纸片 {todoPapers} 张（共 {plan.TodoCount} 条待办）",
            $"- 笔记纸片 {notePapers} 张",
            "",
            "导入不会覆盖或删除任何现有纸片，宿主会生成新 ID。"
        };

        var problems = new List<string>();
        if (plan.UnresolvedLinks > 0)
        {
            problems.Add($"{plan.UnresolvedLinks} 个纸片链接的目标不在本文件中");
        }

        if (plan.PathLinkCount > 0)
        {
            problems.Add($"{plan.PathLinkCount} 条文件/文件夹路径链接");
        }

        if (plan.DroppedReminders > 0)
        {
            problems.Add($"{plan.DroppedReminders} 条已过期提醒");
        }

        if (plan.TruncatedTitles > 0)
        {
            problems.Add($"{plan.TruncatedTitles} 个超长标题将被截断");
        }

        if (problems.Count > 0)
        {
            lines.Add("");
            lines.Add("以下内容无法完整还原：");
            lines.AddRange(problems.Select(p => "- " + p));
        }

        if (plan.Warnings.Count > 0)
        {
            lines.Add("");
            lines.Add($"另有 {plan.Warnings.Count} 条预检提示，详见导入结果。");
        }

        lines.Add("");
        lines.Add("继续导入吗？");
        return string.Join(Environment.NewLine, lines);
    }

    private void ShowImportResult(ImportResult result)
    {
        var lines = new List<string>
        {
            $"新增纸片：{result.CreatedPapers} 张",
            $"失败纸片：{result.FailedPapers} 张",
            $"新增待办：{result.CreatedTodos} 条",
            $"还原链接：{result.RestoredLinks} 条",
            $"还原提醒：{result.RestoredReminders} 条"
        };

        if (result.StoppedEarly && result.FatalError != null)
        {
            lines.Add("");
            lines.Add(result.FatalError);
        }

        lines.AddRange(result.DroppedPathLinkNotes.Take(5).Select(note => "· " + note));
        lines.AddRange(result.Warnings.Take(8).Select(warning => "· " + warning));
        lines.AddRange(result.Failures
            .Take(8)
            .Select(failure => $"· 失败：{failure.Title}（{failure.Code}）：{failure.Message}"));

        if (result.Warnings.Count > 8 || result.Failures.Count > 8)
        {
            lines.Add("· …更多详情已省略。");
        }

        var image = result.StoppedEarly || result.FailedPapers > 0
            ? MessageBoxImage.Warning
            : MessageBoxImage.Information;
        ShowMessage(string.Join(Environment.NewLine, lines), "导入结果", MessageBoxButton.OK, image);
    }

    private MessageBoxResult ShowMessage(
        string text,
        string caption,
        MessageBoxButton button,
        MessageBoxImage image)
    {
        var owner = Window.GetWindow(_view) ?? Application.Current?.MainWindow;
        return owner != null
            ? MessageBox.Show(owner, text, caption, button, image)
            : MessageBox.Show(text, caption, button, image);
    }

    private static string? ResolveDirectory(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
        {
            return path;
        }

        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return Directory.Exists(documents) ? documents : null;
    }

    private static string DescribeError(PaperTodoPluginException ex) => ex.Code switch
    {
        "permission_denied" => $"缺少权限：{ex.Message}",
        "session_closed" => "当前纸片会话已失效。",
        "data_reload_in_progress" => "宿主正在重载数据，请稍后重试。",
        _ => $"读取失败（{ex.Code}）：{ex.Message}"
    };
}
