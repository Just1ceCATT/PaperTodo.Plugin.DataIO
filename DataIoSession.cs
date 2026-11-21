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
/// 内容类型行就地展开具体纸片并多选；所有宿主数据调用都经 HostGateway 切回 UI 线程。
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

    /// <summary>null 表示全部候选纸片；非 null 时按纸片 ID 精确选择。</summary>
    private HashSet<string>? _exportSelection;

    /// <summary>null 表示全部候选；非 null 时按导入计划序号精确选择。</summary>
    private HashSet<string>? _importSelection;

    private ImportPlan? _importPlan;
    private string? _importFilePath;

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
        _view.NoteSelectionChanged += OnNoteSelectionChanged;
        _view.TodoSelectionChanged += OnTodoSelectionChanged;
        _view.PrimaryActionRequested += OnPrimaryActionRequested;
        _view.ChooseImportFileRequested += OnChooseImportFileRequested;

        _view.ApplyTheme(context.Theme, context.Body.Controls);
        _view.SetMode(DataIoMode.Export);
        _view.SetImportFileName(null);
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

    public void CancelInteractions()
    {
        // 宿主在纸片失焦、隐藏或形态切换时会调用 CancelInteractions，用于中止拖拽/输入等
        // 瞬态交互。导入过程中创建并显示新纸片会让工具纸片失焦，因此这里绝不能取消导入任务；
        // 导入只在会话 Dispose 时中止。
    }

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
        _view.NoteSelectionChanged -= OnNoteSelectionChanged;
        _view.TodoSelectionChanged -= OnTodoSelectionChanged;
        _view.PrimaryActionRequested -= OnPrimaryActionRequested;
        _view.ChooseImportFileRequested -= OnChooseImportFileRequested;
    }

    // ---------- 模式与刷新 ----------

    private void OnModeChanged(object? sender, EventArgs e)
    {
        _mode = _view.Mode;

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
                _view.SetCounts("0 条", "0 条");
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

            // 移除已经不存在的纸片选择，避免陈旧 ID。
            if (_exportSelection is { } selection)
            {
                var valid = GetExportCandidates(null).Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
                selection.IntersectWith(valid);
            }

            _view.SetAvailability(
                _gateway.Can(PaperTodoPermissionNames.PapersRead),
                _gateway.Can(PaperTodoPermissionNames.PapersCreate));

            RefreshRows();
            UpdateExportSummary();

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
        _view.SetImportFileName(_importFilePath is null ? null : Path.GetFileName(_importFilePath));

        RefreshRows();
        UpdateImportSummary();

        if (!_busy)
        {
            SetStatus(_importPlan is null
                ? "请选择要导入的文件。"
                : $"已读取 {_importPlan.Papers.Count} 张纸片。");
        }
    }

    // ---------- 候选与选择 ----------

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

    private int DoneCount(string paperId) =>
        _todosByPaper.TryGetValue(paperId, out var todos) ? todos.Count(t => t.Done) : 0;

    private IEnumerable<PaperSnapshot> GetExportCandidates(string? type)
    {
        foreach (var paper in _papers)
        {
            if (IsSelf(paper))
            {
                continue;
            }

            if (string.Equals(paper.Type, "todo", StringComparison.Ordinal))
            {
                if (!_settings.IncludeTodoPapers ||
                    !_gateway.Can(PaperTodoPermissionNames.TodosRead))
                {
                    continue;
                }

                if (type is null || type == "todo")
                {
                    yield return paper;
                }
            }
            else if (string.Equals(paper.Type, "note", StringComparison.Ordinal))
            {
                if (!_settings.IncludeNotePapers ||
                    !IsNoteProviderAllowed(paper) ||
                    !_gateway.Can(PaperTodoPermissionNames.NotesRead))
                {
                    continue;
                }

                if (type is null || type == "note")
                {
                    yield return paper;
                }
            }
        }
    }

    private static string ImportKey(int index) =>
        index.ToString(CultureInfo.InvariantCulture);

    private IEnumerable<(int Index, ImportPaper Paper)> GetImportCandidates(string? type)
    {
        if (_importPlan is null)
        {
            yield break;
        }

        for (var i = 0; i < _importPlan.Papers.Count; i++)
        {
            var paper = _importPlan.Papers[i];
            var kind = (paper.Source.Type ?? "").Trim().ToLowerInvariant();

            if (kind is not ("note" or "todo"))
            {
                continue;
            }

            if (kind == "note" && !_settings.IncludeNotePapers)
            {
                continue;
            }

            if (kind == "todo" && !_settings.IncludeTodoPapers)
            {
                continue;
            }

            if (type is null || type == kind)
            {
                yield return (i, paper);
            }
        }
    }

    private List<PaperSnapshot> GetExportSelectedPapers()
    {
        var candidates = GetExportCandidates(null).ToList();
        if (_exportSelection is null)
        {
            return candidates;
        }

        return candidates.Where(p => _exportSelection.Contains(p.Id)).ToList();
    }

    private List<ImportPaper> GetImportSelectedPapers()
    {
        var candidates = GetImportCandidates(null).ToList();
        if (_importSelection is not null)
        {
            candidates = candidates
                .Where(c => _importSelection.Contains(ImportKey(c.Index)))
                .ToList();
        }

        return candidates.Select(c => c.Paper).ToList();
    }

    // ---------- 行与汇总 ----------

    private void RefreshRows()
    {
        var rows = _mode == DataIoMode.Export ? BuildExportRows() : BuildImportRows();
        var selection = _mode == DataIoMode.Export ? _exportSelection : _importSelection;

        // selection 为 null 时由视图默认全选。
        _view.SetRows(rows, selection);
    }

    private IReadOnlyList<PaperRowItem> BuildExportRows()
    {
        var rows = new List<PaperRowItem>();

        foreach (var paper in GetExportCandidates(null))
        {
            var isNote = string.Equals(paper.Type, "note", StringComparison.Ordinal);
            var isPluginProvider =
                !string.Equals(paper.BodyProviderId, MarkdownProviderId, StringComparison.Ordinal);

            rows.Add(new PaperRowItem
            {
                Key = paper.Id,
                Title = string.IsNullOrWhiteSpace(paper.Title) ? "（无标题）" : paper.Title,
                Type = isNote ? "note" : "todo",
                TypeLabel = isNote ? "笔记" : "待办",
                Summary = isNote
                    ? (isPluginProvider ? "其他插件纸片" : "内置 Markdown 笔记")
                    : (TodoCount(paper.Id) == 0
                        ? "暂无待办"
                        : $"{TodoCount(paper.Id)} 项待办 · {DoneCount(paper.Id)} 项已完成"),
                Warning = isNote && isPluginProvider
                    ? "正文由其他插件提供，导入后为空笔记"
                    : "",
                CanSelect = true
            });
        }

        return rows;
    }

    private IReadOnlyList<PaperRowItem> BuildImportRows()
    {
        var rows = new List<PaperRowItem>();

        foreach (var (index, paper) in GetImportCandidates(null))
        {
            var isNote = string.Equals(paper.Source.Type, "note", StringComparison.OrdinalIgnoreCase);
            var contentLength = paper.Source.Content?.Length ?? 0;
            var todoCount = paper.Source.Todos?.Count ?? 0;

            rows.Add(new PaperRowItem
            {
                Key = ImportKey(index),
                Title = string.IsNullOrWhiteSpace(paper.Source.Title) ? "（无标题）" : paper.Source.Title,
                Type = isNote ? "note" : "todo",
                TypeLabel = isNote ? "笔记" : "待办",
                Summary = isNote
                    ? (contentLength > 0 ? $"{contentLength} 字" : "空笔记")
                    : $"{todoCount} 项待办",
                Warning = isNote && contentLength == 0 ? "文件未包含笔记正文" : "",
                CanSelect = true
            });
        }

        return rows;
    }

    private void UpdateExportSummary()
    {
        var notes = GetExportCandidates("note").ToList();
        var todos = GetExportCandidates("todo").ToList();
        var totalTodoItems = todos.Sum(p => TodoCount(p.Id));

        int selectedNotes;
        int selectedTodoItems;
        if (_exportSelection is null)
        {
            selectedNotes = notes.Count;
            selectedTodoItems = totalTodoItems;
        }
        else
        {
            selectedNotes = notes.Count(p => _exportSelection.Contains(p.Id));
            selectedTodoItems = todos
                .Where(p => _exportSelection.Contains(p.Id))
                .Sum(p => TodoCount(p.Id));
        }

        _view.SetCounts(
            FormatCount(selectedNotes, notes.Count),
            FormatCount(selectedTodoItems, totalTodoItems));
        _view.SetTypeAvailability(
            _gateway.Can(PaperTodoPermissionNames.NotesRead) && notes.Count > 0,
            _gateway.Can(PaperTodoPermissionNames.TodosRead) && todos.Count > 0);

        var selectedPapers = GetExportSelectedPapers();
        _view.SetEstimate(
            $"{selectedNotes} 条笔记 · {selectedTodoItems} 条待办",
            "包含标题、待办与笔记正文等必要元数据");
        _view.SetPrimaryEnabled(selectedPapers.Count > 0);
    }

    private void UpdateImportSummary()
    {
        if (_importPlan is null)
        {
            _view.SetCounts("0 条", "0 条");
            _view.SetTypeAvailability(false, false);
            _view.SetEstimate("尚未选择导入文件", "选择归档或 JSON 文件后显示预估");
            _view.SetPrimaryEnabled(false);
            return;
        }

        var notes = GetImportCandidates("note").ToList();
        var todos = GetImportCandidates("todo").ToList();
        var totalTodoItems = todos.Sum(c => c.Paper.Source.Todos?.Count ?? 0);

        var selected = GetImportSelectedPapers();
        var selectedNotes = selected.Count(
            p => string.Equals(p.Source.Type, "note", StringComparison.OrdinalIgnoreCase));
        var selectedTodoItems = selected
            .Where(p => string.Equals(p.Source.Type, "todo", StringComparison.OrdinalIgnoreCase))
            .Sum(p => p.Source.Todos?.Count ?? 0);

        _view.SetCounts(
            FormatCount(selectedNotes, notes.Count),
            FormatCount(selectedTodoItems, totalTodoItems));
        _view.SetTypeAvailability(
            _gateway.Can(PaperTodoPermissionNames.PapersCreate) && notes.Count > 0,
            _gateway.Can(PaperTodoPermissionNames.PapersCreate) && todos.Count > 0);

        _view.SetEstimate(
            $"{selectedNotes} 条笔记 · {selectedTodoItems} 条待办",
            "只追加新纸片，不覆盖现有数据");
        _view.SetPrimaryEnabled(
            selected.Count > 0 && _gateway.Can(PaperTodoPermissionNames.PapersCreate));
    }

    private static string FormatCount(int selected, int total)
    {
        if (total <= 0)
        {
            return "0 条";
        }

        return selected >= total ? $"{total} 条" : $"已选 {selected} / {total} 条";
    }

    // ---------- 选择变化 ----------

    private void OnNoteSelectionChanged(object? sender, EventArgs e)
    {
        if (_busy)
        {
            return;
        }

        MergeSelection("note");
        UpdateSummary();
    }

    private void OnTodoSelectionChanged(object? sender, EventArgs e)
    {
        if (_busy)
        {
            return;
        }

        MergeSelection("todo");
        UpdateSummary();
    }

    private void MergeSelection(string type)
    {
        if (_mode == DataIoMode.Export)
        {
            var typeIds = GetExportCandidates(type).Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
            _exportSelection ??= GetExportCandidates(null)
                .Select(p => p.Id)
                .ToHashSet(StringComparer.Ordinal);
            _exportSelection.ExceptWith(typeIds);
            _exportSelection.UnionWith(_view.GetSelectedKeys(type));
        }
        else
        {
            var typeKeys = GetImportCandidates(type)
                .Select(c => ImportKey(c.Index))
                .ToHashSet(StringComparer.Ordinal);
            _importSelection ??= GetImportCandidates(null)
                .Select(c => ImportKey(c.Index))
                .ToHashSet(StringComparer.Ordinal);
            _importSelection.ExceptWith(typeKeys);
            _importSelection.UnionWith(_view.GetSelectedKeys(type));
        }
    }

    private void UpdateSummary()
    {
        if (_mode == DataIoMode.Export)
        {
            UpdateExportSummary();
        }
        else
        {
            UpdateImportSummary();
        }
    }

    // ---------- 导出 ----------

    private void OnPrimaryActionRequested(object? sender, EventArgs e) =>
        _ = RunGuardedAsync(_mode == DataIoMode.Export ? ExportAsync : ImportAsync);

    private void OnChooseImportFileRequested(object? sender, EventArgs e) =>
        _ = RunGuardedAsync(ChooseImportFileAsync);

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
            _view.ShowAlertDialog("无法导入", plan.FatalError);
            SetStatus(plan.FatalError);
            return;
        }

        _importPlan = plan;
        _importFilePath = dialog.FileName;
        _importSelection = null;
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
            if (!_view.ShowConfirmDialog("确认导入", confirmation, "继续导入", "取消"))
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
        _importSelection = null;
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

        return string.Join(Environment.NewLine, lines);
    }

    private void ShowImportResult(ImportResult result)
    {
        var stats = new List<(string Label, string Value)>
        {
            ("新增纸片", $"{result.CreatedPapers} 张"),
            ("失败纸片", $"{result.FailedPapers} 张"),
            ("新增待办", $"{result.CreatedTodos} 条"),
            ("还原链接", $"{result.RestoredLinks} 条"),
            ("还原提醒", $"{result.RestoredReminders} 条")
        };

        var lines = new List<string>();
        if (result.StoppedEarly && result.FatalError != null)
        {
            lines.Add(result.FatalError);
        }

        lines.AddRange(result.DroppedPathLinkNotes.Select(note => "· " + note));
        lines.AddRange(result.Warnings.Select(warning => "· " + warning));
        lines.AddRange(result.Failures
            .Select(failure => $"· 失败：{failure.Title}（{failure.Code}）：{failure.Message}"));

        if (lines.Count > 12)
        {
            lines = lines.Take(12).ToList();
            lines.Add("· …更多详情已省略。");
        }

        var title = result.StoppedEarly
            ? "部分导入完成"
            : result.FailedPapers > 0
                ? "导入完成，有失败项"
                : "导入完成";

        _view.ShowResultDialog(
            title,
            stats,
            lines.Count > 0 ? string.Join(Environment.NewLine, lines) : null);
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
