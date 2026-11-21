using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using PaperTodo.Plugin;

namespace PaperTodo.Plugin.DataIo;

/// <summary>
/// 工具纸片会话：负责生命周期、事件订阅以及导出/导入双模式的编排。
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

    private readonly DataIoState _state;
    private DataIoSettings _settings;

    private List<PaperSnapshot> _papers = [];
    private Dictionary<string, TodoSnapshot[]> _todosByPaper = new(StringComparer.Ordinal);

    private DataIoMode _mode = DataIoMode.Export;
    private DataIoScope _scope = DataIoScope.All;
    private bool _includeNotes = true;
    private bool _includeTodos = true;
    private ImportPlan? _importPlan;
    private string? _importFilePath;
    private HashSet<string> _customSelection = new(StringComparer.Ordinal);

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

        _view.ModeChanged += OnModeChanged;
        _view.TypeSelectionChanged += OnTypeSelectionChanged;
        _view.ScopeAllRequested += OnScopeAllRequested;
        _view.ScopeCustomRequested += OnScopeCustomRequested;
        _view.BackRequested += OnBackRequested;
        _view.PrimaryActionRequested += OnPrimaryActionRequested;
        _view.ChooseImportFileRequested += OnChooseImportFileRequested;
        _view.DetailSelectionChanged += OnDetailSelectionChanged;
        _view.FilterChanged += OnFilterChanged;

        _view.SetFilter(_state.Filter, _state.Search);
        _view.ApplyTheme(context.Theme, context.Body.Controls);
        _view.SetMode(DataIoMode.Export);
        _view.SetTypeToggles(true, true);
        _view.SetImportFileName(null);
        _view.SetScope(DataIoScope.All, 0);
        _view.SetPrimaryEnabled(false);

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

        _view.ModeChanged -= OnModeChanged;
        _view.TypeSelectionChanged -= OnTypeSelectionChanged;
        _view.ScopeAllRequested -= OnScopeAllRequested;
        _view.ScopeCustomRequested -= OnScopeCustomRequested;
        _view.BackRequested -= OnBackRequested;
        _view.PrimaryActionRequested -= OnPrimaryActionRequested;
        _view.ChooseImportFileRequested -= OnChooseImportFileRequested;
        _view.DetailSelectionChanged -= OnDetailSelectionChanged;
        _view.FilterChanged -= OnFilterChanged;
    }

    // ---------- 模式与刷新 ----------

    private void OnModeChanged(object? sender, EventArgs e)
    {
        _mode = _view.Mode;
        _scope = DataIoScope.All;
        _customSelection.Clear();
        _view.ShowDetail(false);
        _view.SetScope(DataIoScope.All, 0);

        if (_mode == DataIoMode.Export)
        {
            _ = RefreshAsync();
        }
        else
        {
            RefreshImportView();
        }
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

        if (_mode == DataIoMode.Import)
        {
            RefreshImportView();
            return;
        }

        try
        {
            if (!_gateway.Can(PaperTodoPermissionNames.PapersRead))
            {
                _view.SetAvailability(false, _gateway.Can(PaperTodoPermissionNames.PapersCreate));
                _view.SetCounts(0, 0);
                _view.SetPrimaryEnabled(false);
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
                _view.SetPrimaryEnabled(false);
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

            var canNotes = _gateway.Can(PaperTodoPermissionNames.NotesRead);
            var canTodos = _gateway.Can(PaperTodoPermissionNames.TodosRead);
            if (!canNotes)
            {
                _includeNotes = false;
            }

            if (!canTodos)
            {
                _includeTodos = false;
            }

            _view.SetAvailability(
                _gateway.Can(PaperTodoPermissionNames.PapersRead),
                _gateway.Can(PaperTodoPermissionNames.PapersCreate));
            _view.SetTypeAvailability(canNotes, canTodos);
            _view.SetTypeToggles(_includeNotes, _includeTodos);
            _view.SetCounts(CountExportNotes(), CountExportTodos());

            if (_view.IsDetailVisible)
            {
                RebuildRows();
            }

            UpdateExportEstimate();

            if (!_busy)
            {
                SetStatus($"共 {_papers.Count} 张纸片。");
            }

        }
        catch (Exception ex)
        {
            SetStatus($"刷新列表失败：{ex.Message}");
        }
    }

    private void RefreshImportView()
    {
        if (_disposed)
        {
            return;
        }

        _view.SetAvailability(
            _gateway.Can(PaperTodoPermissionNames.PapersRead),
            _gateway.Can(PaperTodoPermissionNames.PapersCreate));
        _view.SetTypeAvailability(true, true);
        _view.SetTypeToggles(_includeNotes, _includeTodos);
        _view.SetImportFileName(_importFilePath is null ? null : Path.GetFileName(_importFilePath));

        if (_importPlan is null)
        {
            _view.SetCounts(0, 0);
            _view.SetEstimate("尚未选择导入文件", "选择归档或 JSON 文件后显示预估");
            _view.SetPrimaryEnabled(false);
            if (!_busy)
            {
                SetStatus("请选择要导入的文件。");
            }

            return;
        }

        _view.SetCounts(ImportNoteCount(), ImportTodoCount());
        if (_view.IsDetailVisible)
        {
            RebuildRows();
        }

        UpdateImportEstimate();
        if (!_busy)
        {
            SetStatus($"已读取 {_importPlan.Papers.Count} 张纸片。");
        }
    }

    // ---------- 内容统计 ----------

    private bool IsSelf(PaperSnapshot paper) =>
        string.Equals(paper.Id, _context.PaperId, StringComparison.Ordinal);

    private bool IsNoteProviderAllowed(PaperSnapshot paper)
    {
        if (string.Equals(paper.BodyProviderId, MarkdownProviderId, StringComparison.Ordinal))
        {
            return true;
        }

        return _settings.IncludePluginPapers;
    }

    private int TodoCount(string paperId) =>
        _todosByPaper.TryGetValue(paperId, out var todos) ? todos.Length : 0;

    private int CountExportNotes()
    {
        if (!_gateway.Can(PaperTodoPermissionNames.NotesRead))
        {
            return 0;
        }

        return _papers.Count(p =>
            !IsSelf(p) &&
            string.Equals(p.Type, "note", StringComparison.Ordinal) &&
            IsNoteProviderAllowed(p));
    }

    private int CountExportTodos()
    {
        if (!_gateway.Can(PaperTodoPermissionNames.TodosRead))
        {
            return 0;
        }

        return _papers
            .Where(p => !IsSelf(p) && string.Equals(p.Type, "todo", StringComparison.Ordinal))
            .Sum(p => TodoCount(p.Id));
    }

    private int ImportNoteCount() =>
        _importPlan?.Papers.Count(p => string.Equals(p.Source.Type, "note", StringComparison.OrdinalIgnoreCase)) ?? 0;

    private int ImportTodoCount() =>
        _importPlan?.Papers.Sum(p => p.Source.Todos?.Count ?? 0) ?? 0;

    // ---------- 选择集合 ----------

    private bool IsExportIncluded(PaperSnapshot paper)
    {
        if (IsSelf(paper))
        {
            return false;
        }

        if (string.Equals(paper.Type, "todo", StringComparison.Ordinal))
        {
            return _includeTodos && _gateway.Can(PaperTodoPermissionNames.TodosRead);
        }

        if (string.Equals(paper.Type, "note", StringComparison.Ordinal))
        {
            return _includeNotes && IsNoteProviderAllowed(paper);
        }

        return false;
    }

    private List<PaperSnapshot> GetExportSelectedPapers()
    {
        IEnumerable<PaperSnapshot> query = _papers.Where(IsExportIncluded);

        if (_scope == DataIoScope.Custom)
        {
            var keys = new HashSet<string>(_view.GetSelectedKeys(), StringComparer.Ordinal);
            query = query.Where(p => keys.Contains(p.Id));
        }

        return query.ToList();
    }

    private List<ImportPaper> GetImportSelectedPapers()
    {
        if (_importPlan is null)
        {
            return [];
        }

        HashSet<string>? keys = null;
        if (_scope == DataIoScope.Custom)
        {
            keys = new HashSet<string>(_view.GetSelectedKeys(), StringComparer.Ordinal);
        }

        var selected = new List<ImportPaper>();
        for (var i = 0; i < _importPlan.Papers.Count; i++)
        {
            var paper = _importPlan.Papers[i];
            var type = (paper.Source.Type ?? "").Trim().ToLowerInvariant();

            if (type == "note" && !_includeNotes)
            {
                continue;
            }

            if (type == "todo" && !_includeTodos)
            {
                continue;
            }

            if (keys != null && !keys.Contains(i.ToString(CultureInfo.InvariantCulture)))
            {
                continue;
            }

            selected.Add(paper);
        }

        return selected;
    }

    // ---------- 详情行 ----------

    private void RebuildRows()
    {
        _view.SetRows(_mode == DataIoMode.Export ? BuildExportRows() : BuildImportRows());

        if (_scope == DataIoScope.Custom)
        {
            _customSelection = _view.GetSelectedKeys().ToHashSet(StringComparer.Ordinal);
            _view.SetScope(DataIoScope.Custom, _customSelection.Count);
        }
    }

    private IReadOnlyList<PaperRowItem> BuildExportRows()
    {
        var canNotes = _gateway.Can(PaperTodoPermissionNames.NotesRead);
        var canTodos = _gateway.Can(PaperTodoPermissionNames.TodosRead);
        var rows = new List<PaperRowItem>();

        foreach (var paper in _papers)
        {
            if (IsSelf(paper))
            {
                continue;
            }

            if (string.Equals(paper.Type, "todo", StringComparison.Ordinal))
            {
                if (!_includeTodos)
                {
                    continue;
                }

                var todoCount = TodoCount(paper.Id);
                var done = _todosByPaper.TryGetValue(paper.Id, out var todos) ? todos.Count(t => t.Done) : 0;
                rows.Add(new PaperRowItem
                {
                    Key = paper.Id,
                    Title = string.IsNullOrWhiteSpace(paper.Title) ? "（无标题）" : paper.Title,
                    Type = "todo",
                    TypeLabel = "待办",
                    Summary = todoCount == 0
                        ? "暂无待办"
                        : $"{todoCount} 项待办 · {done} 项已完成",
                    Warning = canTodos ? "" : "缺少 todos.read 权限，无法导出待办",
                    CanSelect = canTodos
                });
            }
            else if (string.Equals(paper.Type, "note", StringComparison.Ordinal) && IsNoteProviderAllowed(paper))
            {
                if (!_includeNotes)
                {
                    continue;
                }

                var isPluginProvider =
                    !string.Equals(paper.BodyProviderId, MarkdownProviderId, StringComparison.Ordinal);
                rows.Add(new PaperRowItem
                {
                    Key = paper.Id,
                    Title = string.IsNullOrWhiteSpace(paper.Title) ? "（无标题）" : paper.Title,
                    Type = "note",
                    TypeLabel = "笔记",
                    Summary = isPluginProvider ? "其他插件纸片" : "内置 Markdown 笔记",
                    Warning = isPluginProvider
                        ? "正文由其他插件提供，导入后为空笔记"
                        : (canNotes ? "" : "缺少 notes.read 权限，无法导出正文"),
                    CanSelect = canNotes
                });
            }
        }

        return rows;
    }

    private IReadOnlyList<PaperRowItem> BuildImportRows()
    {
        var rows = new List<PaperRowItem>();
        if (_importPlan is null)
        {
            return rows;
        }

        for (var i = 0; i < _importPlan.Papers.Count; i++)
        {
            var paper = _importPlan.Papers[i];
            var type = (paper.Source.Type ?? "").Trim().ToLowerInvariant();

            if (type == "note" && !_includeNotes)
            {
                continue;
            }

            if (type == "todo" && !_includeTodos)
            {
                continue;
            }

            var todoCount = paper.Source.Todos?.Count ?? 0;
            var contentLength = paper.Source.Content?.Length ?? 0;
            rows.Add(new PaperRowItem
            {
                Key = i.ToString(CultureInfo.InvariantCulture),
                Title = string.IsNullOrWhiteSpace(paper.Source.Title) ? "（无标题）" : paper.Source.Title,
                Type = type,
                TypeLabel = type == "note" ? "笔记" : "待办",
                Summary = type == "note"
                    ? (contentLength > 0 ? $"{contentLength} 字" : "空笔记")
                    : $"{todoCount} 项待办",
                Warning = type == "note" && contentLength == 0 ? "文件未包含笔记正文" : "",
                CanSelect = true
            });
        }

        return rows;
    }

    private void UpdateExportEstimate()
    {
        var selected = GetExportSelectedPapers();
        var notes = selected.Count(p => string.Equals(p.Type, "note", StringComparison.Ordinal));
        var todos = selected.Sum(p => TodoCount(p.Id));

        _view.SetEstimate(
            $"{notes} 条笔记 · {todos} 条待办",
            "包含标题、待办与笔记正文等必要元数据");
        _view.SetPrimaryEnabled(selected.Count > 0);
    }

    private void UpdateImportEstimate()
    {
        if (_importPlan is null)
        {
            _view.SetEstimate("尚未选择导入文件", "选择归档或 JSON 文件后显示预估");
            _view.SetPrimaryEnabled(false);
            return;
        }

        var selected = GetImportSelectedPapers();
        var notes = selected.Count(p => string.Equals(p.Source.Type, "note", StringComparison.OrdinalIgnoreCase));
        var todos = selected.Sum(p => p.Source.Todos?.Count ?? 0);

        _view.SetEstimate(
            $"{notes} 条笔记 · {todos} 条待办",
            "只追加新纸片，不覆盖现有数据");
        _view.SetPrimaryEnabled(
            selected.Count > 0 && _gateway.Can(PaperTodoPermissionNames.PapersCreate));
    }

    // ---------- 用户交互 ----------

    private void OnTypeSelectionChanged(object? sender, EventArgs e)
    {
        if (_busy)
        {
            return;
        }

        _includeNotes = _view.IncludeNotes;
        _includeTodos = _view.IncludeTodos;

        if (_view.IsDetailVisible)
        {
            RebuildRows();
        }

        UpdateEstimate();
    }

    private void OnScopeAllRequested(object? sender, EventArgs e)
    {
        _scope = DataIoScope.All;
        _view.ShowDetail(false);
        _view.SetScope(DataIoScope.All, 0);
        UpdateEstimate();
    }

    private void OnScopeCustomRequested(object? sender, EventArgs e)
    {
        _scope = DataIoScope.Custom;

        if (_mode == DataIoMode.Import && _importPlan is null)
        {
            _scope = DataIoScope.All;
            SetStatus("请先选择导入文件。");
            return;
        }

        RebuildRows();
        if (_customSelection.Count > 0)
        {
            _view.SetSelection(_customSelection);
        }
        else
        {
            _view.SelectAllRows();
        }

        _customSelection = _view.GetSelectedKeys().ToHashSet(StringComparer.Ordinal);
        _view.SetDetailTitle(_mode == DataIoMode.Export ? "选择要导出的纸片" : "选择要导入的纸片");
        _view.ShowDetail(true);
        _view.SetScope(DataIoScope.Custom, _customSelection.Count);
        UpdateEstimate();
    }

    private void OnBackRequested(object? sender, EventArgs e)
    {
        _view.ShowDetail(false);
        _view.SetScope(_scope, _customSelection.Count);
    }

    private void OnDetailSelectionChanged(object? sender, EventArgs e)
    {
        if (_scope != DataIoScope.Custom)
        {
            return;
        }

        _customSelection = _view.GetSelectedKeys().ToHashSet(StringComparer.Ordinal);
        _view.SetScope(DataIoScope.Custom, _customSelection.Count);
        UpdateEstimate();
    }

    private void UpdateEstimate()
    {
        if (_mode == DataIoMode.Export)
        {
            UpdateExportEstimate();
        }
        else
        {
            UpdateImportEstimate();
        }
    }

    private void OnFilterChanged(object? sender, EventArgs e)
    {
        _state.Filter = _view.CurrentFilter;
        _state.Search = _view.CurrentSearch;
        SaveState();
    }

    private void OnPrimaryActionRequested(object? sender, EventArgs e) =>
        _ = RunGuardedAsync(_mode == DataIoMode.Export ? ExportAsync : ImportAsync);

    private void OnChooseImportFileRequested(object? sender, EventArgs e) =>
        _ = RunGuardedAsync(ChooseImportFileAsync);

    // ---------- 导出 ----------

    private async Task ExportAsync()
    {
        if (!_gateway.Can(PaperTodoPermissionNames.PapersRead))
        {
            SetStatus("缺少 papers.read 权限，无法导出。");
            return;
        }

        var selected = GetExportSelectedPapers();
        if (selected.Count == 0)
        {
            SetStatus("没有可导出的内容。");
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "导出纸片数据",
            Filter = "PaperTodo 归档 (*.zip)|*.zip|PaperTodo 数据 (*.json)|*.json",
            DefaultExt = ".zip",
            AddExtension = true,
            FileName = $"papertodo-export-{DateTime.Now:yyyyMMdd-HHmmss}.zip"
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
        var document = await _exportService.CaptureAsync(
            selected.Select(p => p.Id).ToList(),
            _context.PaperId,
            CancellationToken.None);

        SetStatus("正在写入文件…");
        await Task.Run(() => ArchiveService.Write(dialog.FileName, document));

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

    private async Task ChooseImportFileAsync()
    {
        if (!_gateway.Can(PaperTodoPermissionNames.PapersCreate))
        {
            SetStatus("缺少 papers.create 权限，无法导入。");
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "选择导入文件",
            Filter = "PaperTodo 归档 (*.zip;*.json)|*.zip;*.json|所有文件 (*.*)|*.*",
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

        _importPlan = plan;
        _importFilePath = dialog.FileName;
        _scope = DataIoScope.All;
        _customSelection.Clear();
        _view.ShowDetail(false);
        _view.SetScope(DataIoScope.All, 0);
        RefreshImportView();

        SetStatus(plan.Warnings.Count > 0
            ? $"已读取 {plan.Papers.Count} 张纸片；{plan.Warnings.Count} 条预检提示将在导入结果中显示。"
            : $"已读取 {plan.Papers.Count} 张纸片。");
    }

    private async Task ImportAsync()
    {
        if (_importPlan is null)
        {
            SetStatus("请先选择导入文件。");
            return;
        }

        if (!_gateway.Can(PaperTodoPermissionNames.PapersCreate))
        {
            SetStatus("缺少 papers.create 权限，无法导入。");
            return;
        }

        var selected = GetImportSelectedPapers();
        if (selected.Count == 0)
        {
            SetStatus("请选择要导入的内容。");
            return;
        }

        if (_settings.ConfirmBeforeImport)
        {
            var confirmation = BuildConfirmation(selected, _importPlan);
            if (ShowMessage(confirmation, "确认导入", MessageBoxButton.OKCancel, MessageBoxImage.Question) !=
                MessageBoxResult.OK)
            {
                SetStatus("已取消导入。");
                return;
            }
        }

        var filtered = new ImportPlan();
        foreach (var paper in selected)
        {
            filtered.Papers.Add(paper);
        }

        filtered.TodoCount = selected.Sum(p => p.Source.Todos?.Count ?? 0);

        using var cts = new CancellationTokenSource();
        _operation = cts;
        string summary;
        try
        {
            var progress = new Progress<string>(SetStatus);
            var result = await new ImportService(_gateway, progress)
                .ImportAsync(filtered, _settings, cts.Token);

            ShowImportResult(result);
            summary = result.FatalError ??
                $"导入完成：新增 {result.CreatedPapers} 张纸片，失败 {result.FailedPapers} 张。";
        }
        finally
        {
            _operation = null;
        }

        // 导入完成后清空文件选择，避免误触重复导入。
        _importPlan = null;
        _importFilePath = null;
        _scope = DataIoScope.All;
        _customSelection.Clear();
        _view.ShowDetail(false);
        _view.SetScope(DataIoScope.All, 0);

        await RefreshAsync(force: true);
        SetStatus(summary);
    }

    // ---------- 辅助 ----------

    private IDisposable? TrySubscribe()
    {
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

    private static string BuildConfirmation(
        IReadOnlyList<ImportPaper> selected,
        ImportPlan sourcePlan)
    {
        var todoPapers = selected.Count(
            p => string.Equals(p.Source.Type, "todo", StringComparison.OrdinalIgnoreCase));
        var notePapers = selected.Count - todoPapers;
        var todoItems = selected.Sum(p => p.Source.Todos?.Count ?? 0);

        var lines = new List<string>
        {
            $"将向当前工作区追加导入 {selected.Count} 张纸片：",
            $"- 待办纸片 {todoPapers} 张（共 {todoItems} 条待办）",
            $"- 笔记纸片 {notePapers} 张",
            "",
            "导入不会覆盖或删除任何现有纸片，宿主会生成新 ID。"
        };

        var problems = new List<string>();
        if (sourcePlan.UnresolvedLinks > 0)
        {
            problems.Add($"{sourcePlan.UnresolvedLinks} 个纸片链接的目标不在本文件中");
        }

        if (sourcePlan.PathLinkCount > 0)
        {
            problems.Add($"{sourcePlan.PathLinkCount} 条文件/文件夹路径链接");
        }

        if (sourcePlan.DroppedReminders > 0)
        {
            problems.Add($"{sourcePlan.DroppedReminders} 条已过期提醒");
        }

        if (sourcePlan.TruncatedTitles > 0)
        {
            problems.Add($"{sourcePlan.TruncatedTitles} 个超长标题将被截断");
        }

        if (problems.Count > 0)
        {
            lines.Add("");
            lines.Add("以下内容无法完整还原：");
            lines.AddRange(problems.Select(p => "- " + p));
        }

        if (sourcePlan.Warnings.Count > 0)
        {
            lines.Add("");
            lines.Add($"另有 {sourcePlan.Warnings.Count} 条预检提示，详见导入结果。");
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
