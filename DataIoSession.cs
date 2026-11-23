using System.Globalization;
using System.IO;
using System.Text;
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

    /// <summary>纸片转换为本插件后的默认标题。</summary>
    private const string DefaultPaperTitle = "DataIO";

    private readonly PaperBodyContext _context;
    private readonly DataIoView _view;
    private readonly HostGateway _gateway;
    private readonly ExportService _exportService;
    private readonly ImportIdentityService _identity;
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

    /// <summary>预览正文缓存；导出数据变化、切换模式或更换导入文件时整体清空。</summary>
    private readonly Dictionary<string, PreviewContent> _previewCache = new(StringComparer.Ordinal);

    /// <summary>缓存代次：清理后递增，阻止刷新前发出的异步读取把旧内容写回缓存。</summary>
    private int _previewGeneration;

    /// <summary>上一次刷新看到的数据指纹；未变化时保留预览缓存，避免每次宿主事件都重读。</summary>
    private string _dataSignature = "";

    /// <summary>宿主报告过笔记正文变化，需要失效笔记预览缓存。</summary>
    private bool _noteContentDirty;

    private CancellationTokenSource? _operation;
    private bool _busy;
    private bool _disposed;

    /// <summary>列表搜索关键字；空串表示不过滤。</summary>
    private string _searchQuery = "";

    /// <summary>搜索关键字有变化、需要等待防抖后再持久化。</summary>
    private bool _searchDirty;

    /// <summary>插件状态损坏时的提示；只展示一次。</summary>
    private string? _startupWarning;
    private bool _startupWarningShown;

    /// <summary>串行化身份解析，避免设置变化与导入同时重算更新目标。</summary>
    private readonly SemaphoreSlim _resolveLock = new(1, 1);

    public DataIoSession(PaperBodyContext context)
    {
        _context = context;
        _state = DataIoState.Parse(context.StateJson, out var stateRecovered);
        if (stateRecovered)
        {
            _startupWarning = BackupCorruptState(context.StateJson);
        }

        _searchQuery = _state.Search ?? "";
        _settings = DataIoSettings.Parse(context.SettingsJson);

        // 首次把纸片转换为本插件（例如从 Markdown 笔记切换正文）时统一命名为“DataIO”；
        // 标记持久化后不再改名，避免覆盖用户之后手动修改的标题。
        if (!_state.TitleInitialized)
        {
            _state.TitleInitialized = true;
            _context.SetTitle(DefaultPaperTitle);
            _context.SaveStateJson(DataIoState.Serialize(_state));
        }

        _view = new DataIoView();
        _gateway = new HostGateway(context, _view.Dispatcher);
        _exportService = new ExportService(_gateway);
        _identity = new ImportIdentityService(
            _gateway,
            _state,
            id => _context.Workspace.GetPaper(id));
        _view.PreviewLoader = LoadPreviewAsync;

        _view.ModeChanged += OnModeChanged;
        _view.ExportFormatChanged += OnExportFormatChanged;
        _view.NoteSelectionChanged += OnNoteSelectionChanged;
        _view.TodoSelectionChanged += OnTodoSelectionChanged;
        _view.SearchChanged += OnSearchChanged;
        _view.PrimaryActionRequested += OnPrimaryActionRequested;
        _view.ChooseImportFileRequested += OnChooseImportFileRequested;

        _view.ApplyTheme(context.Theme, context.Body.Controls);
        _view.SetMode(DataIoMode.Export);
        _view.SetExportFormat(ParseExportFormat(_state.ExportFormat));
        _view.SetSearch(_searchQuery);
        _view.SetImportFileInfo(null);
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
        if (!visible)
        {
            return;
        }

        // 宿主只在工具纸片可见时投递变更事件，隐藏期间的笔记正文变化收不到 NoteChangedEvent；
        // 恢复可见时强制失效预览缓存，避免悬停预览继续显示旧正文。
        _noteContentDirty = true;
        ScheduleRefresh();
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

        // 设置会直接影响“是否更新已导入的纸片”：已选文件需要按新设置重新解析目标。
        if (!_busy && _mode == DataIoMode.Import && _importPlan is not null)
        {
            _ = ReapplyImportTargetsAsync(_importPlan);
        }

        ScheduleRefresh();
    }

    /// <summary>设置变化后重新解析已选文件的更新目标，避免沿用旧设置下的计划。</summary>
    private async Task ReapplyImportTargetsAsync(ImportPlan plan)
    {
        try
        {
            var resolved = await RefreshUpdateTargetsAsync(plan, CancellationToken.None);
            if (_disposed ||
                _mode != DataIoMode.Import ||
                !ReferenceEquals(_importPlan, plan))
            {
                return;
            }

            RefreshImportView();
            if (!resolved)
            {
                _view.ShowAlertDialog(
                    "无法更新导入计划",
                    "无法校验本地纸片状态，请重新选择导入文件。");
            }
        }
        catch (Exception ex)
        {
            if (!_disposed)
            {
                SetStatus($"重新校验导入计划失败：{ex.Message}");
            }
        }
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
        _previewCache.Clear();
        _view.PreviewLoader = null;

        _view.ModeChanged -= OnModeChanged;
        _view.ExportFormatChanged -= OnExportFormatChanged;
        _view.NoteSelectionChanged -= OnNoteSelectionChanged;
        _view.TodoSelectionChanged -= OnTodoSelectionChanged;
        _view.SearchChanged -= OnSearchChanged;
        _view.PrimaryActionRequested -= OnPrimaryActionRequested;
        _view.ChooseImportFileRequested -= OnChooseImportFileRequested;
    }

    // ---------- 模式与刷新 ----------

    private void OnModeChanged(object? sender, EventArgs e)
    {
        _mode = _view.Mode;

        // 导出用纸片 ID、导入用计划序号作为缓存键，切换模式必须清空。
        InvalidatePreviewCache();

        if (_mode == DataIoMode.Export)
        {
            _ = RefreshAsync();
        }
        else
        {
            RefreshImportView();
        }
    }

    private void OnExportFormatChanged(object? sender, EventArgs e)
    {
        _state.ExportFormat = _view.ExportFormat == DataIoExportFormat.Markdown
            ? "markdown"
            : "json";
        SaveState();
    }

    private void OnDebounceTick(object? sender, EventArgs e)
    {
        _debounce.Stop();

        // 搜索输入较频繁，统一等到防抖窗口结束再持久化，减少状态写入。
        if (_searchDirty)
        {
            _searchDirty = false;
            SaveState();
        }

        _ = RefreshAsync();
    }

    private void OnSearchChanged(object? sender, EventArgs e)
    {
        var query = _view.SearchText.Trim();
        if (string.Equals(query, _searchQuery, StringComparison.Ordinal))
        {
            return;
        }

        _searchQuery = query;
        _state.Search = query;
        _searchDirty = true;
        ScheduleRefresh();
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

            ReconcileImportLinks();

            // 只有数据真的变化时才丢弃预览缓存；否则勾选、搜索等操作会让悬停预览反复重读。
            var signature = ComputeDataSignature();
            if (_noteContentDirty || !string.Equals(signature, _dataSignature, StringComparison.Ordinal))
            {
                _dataSignature = signature;
                _noteContentDirty = false;
                InvalidatePreviewCache();
            }

            // 移除已经不存在的纸片选择，避免陈旧 ID；搜索过滤不应影响已选集合。
            if (_exportSelection is { } selection)
            {
                var valid = GetExportCandidates(null, ignoreSearch: true)
                    .Select(p => p.Id)
                    .ToHashSet(StringComparer.Ordinal);
                selection.IntersectWith(valid);
            }

            _view.SetAvailability(
                _gateway.Can(PaperTodoPermissionNames.PapersRead),
                _gateway.Can(PaperTodoPermissionNames.PapersCreate));

            RefreshRows();
            UpdateExportSummary();

            if (!_busy)
            {
                SetStatus(StartupPrefix() + $"共 {_papers.Count} 张纸片。");
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
        _view.SetImportFileInfo(
            _importFilePath is null ? null : Path.GetFileName(_importFilePath),
            DescribeImportFile());

        RefreshRows();
        UpdateImportSummary();

        if (!_busy)
        {
            SetStatus(StartupPrefix() + (_importPlan is null
                ? "请选择要导入的文件。"
                : $"已读取 {_importPlan.Papers.Count} 张纸片。"));
        }
    }

    /// <summary>
    /// 用刚拉取的纸片列表对账导入映射。宿主在工具纸片隐藏期间会丢弃变更事件，
    /// 删除可能从未送达，因此每次刷新列表时主动校验一次映射目标。
    /// </summary>
    private void ReconcileImportLinks()
    {
        // 工具纸片自身也在列表中，列表为空只可能是宿主数据尚未就绪的瞬态。
        // 此时绝不能把全部映射判定为失效，等待下次刷新再对账。
        if (_papers.Count == 0)
        {
            return;
        }

        var papersById = new Dictionary<string, PaperSnapshot>(StringComparer.Ordinal);
        foreach (var paper in _papers)
        {
            papersById[paper.Id] = paper;
        }

        _identity.Reconcile(papersById);
        if (_identity.ConsumeChanges())
        {
            SaveState();
        }
    }

    /// <summary>首次展示状态栏时带上“状态已损坏并备份”的提示。</summary>
    private string StartupPrefix()
    {
        if (_startupWarningShown || _startupWarning is null)
        {
            return "";
        }

        _startupWarningShown = true;
        return _startupWarning + " ";
    }

    /// <summary>文件行的次要状态：格式与已解析纸片数；未选择文件时返回 null。</summary>
    private string? DescribeImportFile()
    {
        if (_importFilePath is null)
        {
            return null;
        }

        var extension = Path.GetExtension(_importFilePath);
        var format = string.Equals(extension, ".md", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(extension, ".markdown", StringComparison.OrdinalIgnoreCase)
            ? "Markdown"
            : "JSON";
        return _importPlan is null ? format : $"{format} · {_importPlan.Papers.Count} 张纸片";
    }

    // ---------- 候选与选择 ----------

    private bool IsSelf(PaperSnapshot paper) =>
        string.Equals(paper.Id, _context.PaperId, StringComparison.Ordinal);

    private int TodoCount(string paperId) =>
        _todosByPaper.TryGetValue(paperId, out var todos) ? todos.Length : 0;

    private int DoneCount(string paperId) =>
        _todosByPaper.TryGetValue(paperId, out var todos) ? todos.Count(t => t.Done) : 0;

    private IEnumerable<PaperSnapshot> GetExportCandidates(string? type, bool ignoreSearch = false)
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

                if ((type is null || type == "todo") &&
                    (ignoreSearch || MatchesExportSearch(paper)))
                {
                    yield return paper;
                }
            }
            else if (string.Equals(paper.Type, "note", StringComparison.Ordinal))
            {
                if (!_settings.IncludeNotePapers ||
                    !string.Equals(paper.BodyProviderId, MarkdownProviderId, StringComparison.Ordinal) ||
                    !_gateway.Can(PaperTodoPermissionNames.NotesRead))
                {
                    continue;
                }

                if ((type is null || type == "note") &&
                    (ignoreSearch || MatchesExportSearch(paper)))
                {
                    yield return paper;
                }
            }
        }
    }

    /// <summary>导出检索：匹配标题或待办文本（笔记正文需读取宿主才可知，不参与过滤）。</summary>
    private bool MatchesExportSearch(PaperSnapshot paper)
    {
        if (_searchQuery.Length == 0)
        {
            return true;
        }

        if (paper.Title.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return _todosByPaper.TryGetValue(paper.Id, out var todos) &&
               todos.Any(todo => todo.Text.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase));
    }

    private static string ImportKey(int index) =>
        index.ToString(CultureInfo.InvariantCulture);

    private IEnumerable<(int Index, ImportPaper Paper)> GetImportCandidates(
        string? type,
        bool ignoreSearch = false)
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

            if ((type is null || type == kind) &&
                (ignoreSearch || MatchesImportSearch(paper)))
            {
                yield return (i, paper);
            }
        }
    }

    /// <summary>导入检索：匹配标题、笔记正文或待办文本。</summary>
    private bool MatchesImportSearch(ImportPaper paper)
    {
        if (_searchQuery.Length == 0)
        {
            return true;
        }

        var source = paper.Source;
        if ((source.Title ?? "").Contains(_searchQuery, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if ((source.Content ?? "").Contains(_searchQuery, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return (source.Todos ?? [])
            .Any(todo => todo.Text.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>缺少 papers.create 时，只有已命中更新目标的纸片仍可选择。</summary>
    private bool IsImportSelectable(ImportPaper paper) =>
        !string.IsNullOrWhiteSpace(paper.UpdateTargetId) ||
        _gateway.Can(PaperTodoPermissionNames.PapersCreate);

    /// <summary>
    /// 命中更新目标、但内容与本地完全一致的笔记：更新是空操作，
    /// 不应出现在更新预览里，否则会被误认为存在差异。
    /// </summary>
    internal static bool IsNoOpUpdate(ImportPaper paper) =>
        !string.IsNullOrWhiteSpace(paper.UpdateTargetId) &&
        !paper.UpdateHasChanges &&
        string.Equals(paper.Source.Type, "note", StringComparison.OrdinalIgnoreCase);

    private static string DisplayImportTitle(ImportPaper paper) =>
        string.IsNullOrWhiteSpace(paper.Source.Title) ? "（无标题）" : paper.Source.Title;

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
        var candidates = GetImportCandidates(null);
        if (_importSelection is not null)
        {
            candidates = candidates.Where(c => _importSelection.Contains(ImportKey(c.Index)));
        }

        // 无 papers.create 时，“全选”只覆盖可更新的纸片，避免统计与实际执行不一致。
        return candidates
            .Where(c => IsImportSelectable(c.Paper))
            .Select(c => c.Paper)
            .ToList();
    }

    // ---------- 行与汇总 ----------

    private void RefreshRows()
    {
        var rows = _mode == DataIoMode.Export ? BuildExportRows() : BuildImportRows();
        var selection = _mode == DataIoMode.Export ? _exportSelection : _importSelection;

        // selection 为 null 时由视图默认全选。
        _view.SetRows(rows, selection);
    }

    /// <summary>清空预览缓存并递增代次；用于模式切换、更换导入文件等键空间变化。</summary>
    private void InvalidatePreviewCache()
    {
        _previewCache.Clear();
        _previewGeneration++;
    }

    /// <summary>纸片与待办快照的轻量指纹，用于判断预览缓存是否仍然有效。</summary>
    private string ComputeDataSignature()
    {
        const char fieldSeparator = (char)0x1f;
        const char recordSeparator = (char)0x1e;

        var builder = new StringBuilder(_papers.Count * 64);
        foreach (var paper in _papers)
        {
            builder
                .Append(paper.Id).Append(fieldSeparator)
                .Append(paper.Title).Append(fieldSeparator)
                .Append(paper.Type).Append(fieldSeparator)
                .Append(paper.BodyProviderId).Append(fieldSeparator)
                .Append(paper.IsVisible ? '1' : '0')
                .Append(recordSeparator);
        }

        foreach (var (paperId, todos) in _todosByPaper)
        {
            builder.Append(paperId).Append(fieldSeparator);
            foreach (var todo in todos)
            {
                builder
                    .Append(todo.Id).Append(fieldSeparator)
                    .Append(todo.Text).Append(fieldSeparator)
                    .Append(todo.Done ? '1' : '0').Append(fieldSeparator)
                    .Append(todo.Order).Append(fieldSeparator)
                    .Append(todo.ReminderAt?.ToUnixTimeSeconds() ?? 0).Append(fieldSeparator)
                    .Append(todo.LinkedPaperId).Append(fieldSeparator)
                    .Append(todo.LinkedPath).Append(recordSeparator);
            }
        }

        return builder.ToString();
    }

    private IReadOnlyList<PaperRowItem> BuildExportRows()
    {
        var rows = new List<PaperRowItem>();

        foreach (var paper in GetExportCandidates(null))
        {
            var isNote = string.Equals(paper.Type, "note", StringComparison.Ordinal);

            rows.Add(new PaperRowItem
            {
                Key = paper.Id,
                Title = string.IsNullOrWhiteSpace(paper.Title) ? "（无标题）" : paper.Title,
                Type = isNote ? "note" : "todo",
                TypeLabel = isNote ? "笔记" : "待办",
                Summary = isNote
                    ? "内置 Markdown 笔记"
                    : (TodoCount(paper.Id) == 0
                        ? "暂无待办"
                        : $"{TodoCount(paper.Id)} 项待办 · {DoneCount(paper.Id)} 项已完成"),
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
            var isUpdate = !string.IsNullOrWhiteSpace(paper.UpdateTargetId);

            var selectable = IsImportSelectable(paper);
            var warnings = new List<string>();
            if (isNote && contentLength == 0)
            {
                warnings.Add("文件未包含笔记正文");
            }

            if (paper.TargetLocallyModified)
            {
                warnings.Add("本地已修改，更新会覆盖");
            }

            if (paper.TargetImagesAtRisk)
            {
                warnings.Add("图片引用会被破坏，只能跳过或新增");
            }

            if (!selectable)
            {
                warnings.Add("缺少 papers.create 权限，无法新建");
            }

            rows.Add(new PaperRowItem
            {
                Key = ImportKey(index),
                Title = string.IsNullOrWhiteSpace(paper.Source.Title) ? "（无标题）" : paper.Source.Title,
                Type = isNote ? "note" : "todo",
                TypeLabel = isNote ? "笔记" : "待办",
                Summary = (isNote
                        ? (contentLength > 0 ? $"{contentLength} 字" : "空笔记")
                        : $"{todoCount} 项待办") +
                    (isUpdate ? " · 将更新" : ""),
                Warning = string.Join("；", warnings),
                CanSelect = selectable
            });
        }

        return rows;
    }

    // ---------- 预览 ----------

    /// <summary>按需读取一张纸片的预览内容；结果按 key 缓存，列表变化时整体失效。</summary>
    private async Task<PreviewContent?> LoadPreviewAsync(string key)
    {
        if (_disposed)
        {
            return null;
        }

        if (_previewCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var generation = _previewGeneration;
        var mode = _mode;
        var content = mode == DataIoMode.Export
            ? await BuildExportPreviewAsync(key)
            : PreviewFactory.BuildImport(_importPlan, key);

        if (content is not null && !_disposed && generation == _previewGeneration)
        {
            _previewCache[key] = content;
        }

        return content;
    }

    private Task<PreviewContent?> BuildExportPreviewAsync(string paperId)
    {
        var paper = _papers.FirstOrDefault(p => string.Equals(p.Id, paperId, StringComparison.Ordinal));
        var todos = _todosByPaper.TryGetValue(paperId, out var stored) ? stored : null;
        return PreviewFactory.BuildExportAsync(_gateway, paper, todos);
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
            _view.SetEstimate("尚未选择导入文件", "选择 JSON 或 Markdown 文件后显示预估");
            _view.SetPrimaryEnabled(false);
            _view.SetImageWarning(null);
            return;
        }

        _view.SetImageWarning(_importPlan.ImageReferenceCount > 0
            ? $"文件中有 {_importPlan.NotesWithImages} 张笔记包含 {_importPlan.ImageReferenceCount} 处图片引用。" +
              "导出文件只保存正文文本，图片本体不在文件中；导入到其他设备或原图已清理后将无法显示。"
            : null);

        // 总数只统计当前可选择的条目，避免“已选 1 / 3”里混入根本无法导入的项。
        var notes = GetImportCandidates("note")
            .Where(c => IsImportSelectable(c.Paper))
            .ToList();
        var todos = GetImportCandidates("todo")
            .Where(c => IsImportSelectable(c.Paper))
            .ToList();
        var totalTodoItems = todos.Sum(c => c.Paper.Source.Todos?.Count ?? 0);

        var selected = GetImportSelectedPapers();
        var selectedNotes = selected.Count(
            p => string.Equals(p.Source.Type, "note", StringComparison.OrdinalIgnoreCase));
        var selectedTodoItems = selected
            .Where(p => string.Equals(p.Source.Type, "todo", StringComparison.OrdinalIgnoreCase))
            .Sum(p => p.Source.Todos?.Count ?? 0);

        var updateCount = selected.Count(p => !string.IsNullOrWhiteSpace(p.UpdateTargetId));
        var createCount = selected.Count - updateCount;

        _view.SetCounts(
            FormatCount(selectedNotes, notes.Count),
            FormatCount(selectedTodoItems, totalTodoItems));
        _view.SetTypeAvailability(notes.Count > 0, todos.Count > 0);

        // 文案必须如实反映本次是“新建”还是“更新”，不能永远宣称只追加。
        var rule = updateCount > 0
            ? (createCount > 0
                ? $"将新建 {createCount} 张、更新 {updateCount} 张现有纸片"
                : $"将更新 {updateCount} 张现有纸片，不新建")
            : "只追加新纸片，不覆盖现有数据";

        _view.SetEstimate(
            $"{selectedNotes} 条笔记 · {selectedTodoItems} 条待办",
            rule);
        _view.SetPrimaryEnabled(selected.Count > 0);
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
            // 只合并当前可见（搜索命中）的条目；初始化时保留全部候选，
            // 避免搜索过滤后切换勾选把未显示的条目误判为取消。
            var typeIds = GetExportCandidates(type).Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
            _exportSelection ??= GetExportCandidates(null, ignoreSearch: true)
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
            _importSelection ??= GetImportCandidates(null, ignoreSearch: true)
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

    private void OnChooseImportFileRequested(object? sender, EventArgs e)
    {
        SetStatus("正在打开文件选择器…");
        _ = RunGuardedAsync(ChooseImportFileAsync);
    }

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

        var markdown = _view.ExportFormat == DataIoExportFormat.Markdown;
        var extension = markdown ? ".md" : ".json";
        var dialog = new SaveFileDialog
        {
            Title = "导出纸片数据",
            Filter = markdown
                ? "Markdown 文档 (*.md)|*.md"
                : "PaperTodo 数据 (*.json)|*.json",
            DefaultExt = extension,
            AddExtension = true,
            FileName = $"papertodo-export-{DateTime.Now:yyyyMMdd-HHmmss}{extension}"
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

        var formatLabel = markdown ? "Markdown" : "JSON";

        // 关闭纸片时取消导出，避免在已销毁的会话上继续弹结果框。
        using var cts = new CancellationTokenSource();
        _operation = cts;

        SetStatus("正在读取数据…");
        var document = await _exportService.CaptureAsync(
            selected.Select(p => p.Id).ToList(),
            _context.PaperId,
            cts.Token);
        cts.Token.ThrowIfCancellationRequested();

        SetStatus("正在写入文件…");
        await Task.Run(() =>
        {
            if (markdown)
            {
                MarkdownExportService.Write(dialog.FileName, document);
            }
            else
            {
                DataFileService.Write(dialog.FileName, document);
            }
        }, cts.Token);

        if (_disposed)
        {
            return;
        }

        var imageSummary = ImageReferenceSummary.FromNotes(
            document.Papers
                .Where(p => string.Equals(p.Type, "note", StringComparison.Ordinal))
                .Select(p => p.Content));

        var unavailableNotes = document.Papers.Count(
            p => string.Equals(p.Type, "note", StringComparison.Ordinal) && p.ContentAvailable == false);
        var status = $"已导出 {document.Papers.Count} 张纸片（{formatLabel}）：{dialog.FileName}";
        if (unavailableNotes > 0)
        {
            status += $"（{unavailableNotes} 张笔记正文不可读取，已按空正文导出）";
        }

        if (imageSummary.HasAny)
        {
            status += $"（{imageSummary.NoteCount} 张笔记含 {imageSummary.ReferenceCount} 处图片引用，图片未包含在导出文件中）";
        }

        if (markdown)
        {
            status += "（不包含提醒、关联纸片、可见性等数据）";
        }

        SetStatus(status);

        var stats = new List<(string Label, string Value)>
        {
            ("导出纸片", $"{document.Papers.Count} 张"),
            ("导出格式", formatLabel)
        };
        if (unavailableNotes > 0)
        {
            stats.Add(("正文不可读", $"{unavailableNotes} 张"));
        }

        if (imageSummary.HasAny)
        {
            stats.Add(("含图片笔记", $"{imageSummary.NoteCount} 张"));
            stats.Add(("图片引用", $"{imageSummary.ReferenceCount} 处"));
        }

        // 每次都明确说明导出文件不是完整备份，避免用户把它当作唯一的数据保全手段。
        var details = new List<string>
        {
            $"文件：{dialog.FileName}",
            "注意：导出文件不是完整备份——图片本体、由其他插件提供正文的笔记、" +
            "以及待办关联的文件/文件夹路径不会随文件保存。"
        };
        if (markdown)
        {
            details.Add(
                "Markdown 只保留标题、待办勾选状态和笔记正文；" +
                "提醒、关联纸片、可见性等 PaperTodo 专有数据不会保留。");
        }

        if (imageSummary.HasAny)
        {
            details.Add(markdown
                ? "图片本体未包含在文件中；正文中的图片引用已原样保留，但换设备或原图被清理后将无法显示。"
                : "图片本体未包含在文件中。导入到其他设备、或原图已被清理后，图片将无法显示；" +
                  "如果图片 ID 被复用，还可能显示成其他图片。");
        }

        _view.ShowResultDialog("导出完成", stats, details);
    }

    // ---------- 导入 ----------

    private async Task ChooseImportFileAsync()
    {
        // 选择文件本身不需要 papers.create；纯更新导入也应允许继续。
        var dialog = new OpenFileDialog
        {
            Title = "选择导入文件",
            // 默认过滤器同时列出 JSON 与 Markdown，避免打开对话框后只能看到其中一种。
            Filter = "PaperTodo 数据 (*.json;*.md;*.markdown)|*.json;*.md;*.markdown|"
                + "JSON 文件 (*.json)|*.json|"
                + "Markdown 文档 (*.md;*.markdown)|*.md;*.markdown|"
                + "所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        var initial = ResolveDirectory(_state.LastImportDirectory);
        if (initial != null)
        {
            dialog.InitialDirectory = initial;
        }

        SetStatus("正在打开文件选择器…");
        if (dialog.ShowDialog(Window.GetWindow(_view)) != true)
        {
            SetStatus("已取消选择文件。");
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

        // 按当前设置解析可更新的本地纸片；解析失败时按全部新建处理并给出提示。
        if (!await RefreshUpdateTargetsAsync(plan, CancellationToken.None))
        {
            plan.Warnings.Add("无法校验本地更新目标，本次将按新建处理。");
        }

        _importPlan = plan;
        _importFilePath = dialog.FileName;
        _importSelection = null;
        InvalidatePreviewCache();
        RefreshImportView();

        var updateCount = plan.Papers.Count(paper => !string.IsNullOrWhiteSpace(paper.UpdateTargetId));
        var updateNote = updateCount > 0 ? $"（其中 {updateCount} 张将更新已有纸片）" : "";
        SetStatus(plan.Warnings.Count > 0
            ? $"已读取 {plan.Papers.Count} 张纸片{updateNote}；{plan.Warnings.Count} 条预检提示将在导入结果中显示。"
            : $"已读取 {plan.Papers.Count} 张纸片{updateNote}。");
    }

    /// <summary>清空计划内的更新目标及所有派生状态。</summary>
    internal static void ClearUpdateTargets(ImportPlan plan)
    {
        foreach (var paper in plan.Papers)
        {
            paper.UpdateTargetId = null;
            paper.UpdateTargetTitle = null;
            paper.UpdateTargetFingerprint = null;
            paper.TargetLocallyModified = false;
            paper.TargetImagesAtRisk = false;
            paper.UpdateDiff = null;
            paper.UpdateHasChanges = false;
        }
    }

    /// <summary>
    /// 按当前设置与当前纸片状态重新解析更新目标。
    /// 返回 false 表示解析失败；调用方应中止导入，而不是继续使用陈旧计划。
    /// </summary>
    private async Task<bool> RefreshUpdateTargetsAsync(
        ImportPlan plan,
        CancellationToken cancellationToken)
    {
        await _resolveLock.WaitAsync(cancellationToken);
        try
        {
            ClearUpdateTargets(plan);

            if (!_settings.UpdateImportedPapers)
            {
                return true;
            }

            if (!_gateway.Can(PaperTodoPermissionNames.PapersRead))
            {
                // 没有读取权限时只能按新建处理。
                return true;
            }

            try
            {
                await _identity.ResolveAsync(plan, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 解析可能只完成了一部分；清空目标，避免把半成品计划当成有效结果。
                ClearUpdateTargets(plan);
                return false;
            }

            if (_identity.ConsumeChanges())
            {
                SaveState();
            }

            return true;
        }
        finally
        {
            _resolveLock.Release();
        }
    }

    private async Task ImportAsync()
    {
        if (_importPlan is null)
        {
            SetStatus("请先选择导入文件。");
            return;
        }

        // 选择文件到点击导入之间本地可能已变化；先按当前状态重新解析更新目标与指纹。
        if (!await RefreshUpdateTargetsAsync(_importPlan, CancellationToken.None))
        {
            SetStatus("无法重新校验本地纸片状态，已中止导入，请稍后重试。");
            return;
        }

        RefreshImportView();

        var selected = GetImportSelectedPapers();
        if (selected.Count == 0)
        {
            SetStatus("请选择要导入的内容。");
            return;
        }

        // 命中更新目标但内容已一致的笔记没有可更新内容，不进入更新预览，也不计入导入。
        var unchangedPapers = selected.Where(IsNoOpUpdate).ToList();
        if (unchangedPapers.Count > 0)
        {
            selected = selected.Where(paper => !IsNoOpUpdate(paper)).ToList();
            if (selected.Count == 0)
            {
                var unchangedTitles = unchangedPapers.Select(DisplayImportTitle).ToList();
                _view.ShowResultDialog(
                    "内容无变化",
                    new List<(string Label, string Value)>
                    {
                        ("无变化纸片", $"{unchangedTitles.Count} 张")
                    },
                    unchangedTitles.Select(title => "· " + title).ToList());
                SetStatus($"选中的 {unchangedTitles.Count} 张笔记内容与本地一致，未做任何修改。");
                return;
            }
        }

        // 纯更新不依赖 papers.create；只有确实要新建时才检查该权限。
        if (selected.Any(paper => string.IsNullOrWhiteSpace(paper.UpdateTargetId)) &&
            !_gateway.Can(PaperTodoPermissionNames.PapersCreate))
        {
            SetStatus("缺少 papers.create 权限，无法新建纸片；可只勾选“将更新”的纸片。");
            return;
        }

        // 有更新目标时先展示差异，并让用户逐张选择跳过/更新/新增。
        var skippedTitles = unchangedPapers.Select(DisplayImportTitle).ToList();
        var updateCandidates = selected
            .Where(paper => !string.IsNullOrWhiteSpace(paper.UpdateTargetId))
            .ToList();
        var updatePreviewShown = updateCandidates.Count > 0;
        if (updatePreviewShown)
        {
            // 记录预览时的目标与指纹；对话框关闭后若发生变化，取消本次导入。
            var previewSnapshot = updateCandidates.ToDictionary(
                paper => paper,
                paper => (paper.UpdateTargetId, paper.UpdateTargetFingerprint));

            // 关闭删除时，更新预览里的删除行不会真正删除，需要明确告知，避免误读差异。
            IReadOnlyList<string> previewWarnings = _importPlan.Warnings;
            if (!_settings.DeleteMissingTodos &&
                updateCandidates.Any(paper =>
                    string.Equals(paper.Source.Type, "todo", StringComparison.OrdinalIgnoreCase)))
            {
                previewWarnings = [.. _importPlan.Warnings,
                    "“更新时删除文件中不存在的待办”已关闭：更新不会删除本地多出的待办，只新增和修改。"];
            }

            var choices = _view.ShowUpdatePreviewDialog(
                updateCandidates,
                ImportSummaryBuilder.BuildUnrecoverable(selected, _importPlan),
                _gateway.Can(PaperTodoPermissionNames.PapersCreate),
                previewWarnings);
            if (choices == null)
            {
                SetStatus("已取消导入。");
                return;
            }

            // 预览期间本地纸片可能被修改/删除，重新解析后再应用用户选择。
            if (!await RefreshUpdateTargetsAsync(_importPlan, CancellationToken.None))
            {
                RefreshImportView();
                SetStatus("无法重新校验本地纸片状态，已取消导入。");
                _view.ShowAlertDialog(
                    "无法继续导入",
                    "无法重新校验本地纸片状态，请稍后重试。");
                return;
            }

            var targetsChanged = false;
            var kept = new List<ImportPaper>(selected.Count);
            foreach (var paper in selected)
            {
                if (string.IsNullOrWhiteSpace(paper.UpdateTargetId))
                {
                    // 原本有更新目标、却在预览期间消失：按用户当时的选择处理。
                    if (previewSnapshot.ContainsKey(paper))
                    {
                        var lostChoice = choices.TryGetValue(paper, out var chosenOnLost)
                            ? chosenOnLost
                            : ImportUpdateAction.Skip;

                        if (lostChoice == ImportUpdateAction.Update)
                        {
                            targetsChanged = true;
                        }
                        else if (lostChoice == ImportUpdateAction.Skip)
                        {
                            skippedTitles.Add(DisplayImportTitle(paper));
                            continue;
                        }
                    }

                    kept.Add(paper);
                    continue;
                }

                // 兜底也按“跳过”处理：覆盖本地数据只能是用户显式选择。
                var action = choices.TryGetValue(paper, out var chosen)
                    ? chosen
                    : ImportUpdateAction.Skip;
                switch (action)
                {
                    case ImportUpdateAction.Skip:
                        skippedTitles.Add(string.IsNullOrWhiteSpace(paper.Source.Title)
                            ? "（无标题）"
                            : paper.Source.Title);
                        break;
                    case ImportUpdateAction.CreateNew:
                        paper.UpdateTargetId = null;
                        paper.UpdateTargetTitle = null;
                        paper.UpdateTargetFingerprint = null;
                        paper.TargetLocallyModified = false;
                        paper.TargetImagesAtRisk = false;
                        paper.UpdateDiff = null;
                        paper.UpdateHasChanges = false;
                        kept.Add(paper);
                        break;
                    default:
                        if (previewSnapshot.TryGetValue(paper, out var before) &&
                            (!string.Equals(before.UpdateTargetId, paper.UpdateTargetId, StringComparison.Ordinal) ||
                             !string.Equals(before.UpdateTargetFingerprint, paper.UpdateTargetFingerprint, StringComparison.Ordinal)))
                        {
                            targetsChanged = true;
                        }

                        kept.Add(paper);
                        break;
                }
            }

            if (targetsChanged)
            {
                RefreshImportView();
                SetStatus("导入已取消：本地纸片在确认期间发生了变化。");
                _view.ShowAlertDialog(
                    "本地状态已变化",
                    "本地纸片在确认期间被修改或删除，已取消本次导入。请重新导入文件并再次确认。");
                return;
            }

            selected = kept;
            if (selected.Count == 0)
            {
                _view.ShowResultDialog(
                    "已跳过更新",
                    new List<(string Label, string Value)>
                    {
                        ("跳过纸片", $"{skippedTitles.Count} 张")
                    },
                    skippedTitles.Select(title => "· " + title).ToList());
                SetStatus($"已跳过 {skippedTitles.Count} 张纸片，未导入任何内容。");
                return;
            }
        }

        if (_settings.ConfirmBeforeImport && !updatePreviewShown)
        {
            var confirmation = ImportSummaryBuilder.BuildConfirmation(selected, _importPlan);
            if (!_view.ShowConfirmDialog(
                    "确认导入",
                    confirmation,
                    "继续导入",
                    "取消",
                    warnings: _importPlan.Warnings))
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

        var imageSummary = ImageReferenceSummary.FromNotes(
            selected
                .Where(p => string.Equals(p.Source.Type, "note", StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Source.Content));

        using var cts = new CancellationTokenSource();
        _operation = cts;
        string summary;
        try
        {
            var progress = new Progress<string>(SetStatus);
            var result = await new ImportService(_gateway, progress)
                .ImportAsync(filtered, _settings, cts.Token);

            if (unchangedPapers.Count > 0)
            {
                result.Warnings.Insert(0,
                    $"{unchangedPapers.Count} 张笔记内容与本地一致，已自动跳过。");
            }

            result.SkippedPapers = skippedTitles.Count;
            result.SkippedTitles.AddRange(skippedTitles);

            if (imageSummary.HasAny)
            {
                result.Warnings.Insert(0,
                    $"文件中有 {imageSummary.NoteCount} 张笔记包含 {imageSummary.ReferenceCount} 处图片引用；" +
                    "图片本体未随文件导入，跨设备或原图已清理时可能无法显示。");
            }

            // 会话已关闭（导入被取消）时不再登记映射或弹窗：此时保存状态与展示界面都没有意义。
            if (!_disposed)
            {
                try
                {
                    // 导入（含顺序/关联/提醒修复）完成后登记身份映射，供下次导入更新同一批纸片。
                    await _identity.RecordAsync(selected, result.SourceIdToPaperId, CancellationToken.None);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // 映射登记失败不影响已完成的导入，但要让用户知道下次可能产生重复纸片。
                    result.Warnings.Add(
                        "导入已完成，但未能登记更新映射；下次导入同一文件可能会新建重复纸片。");
                }

                if (_identity.ConsumeChanges())
                {
                    SaveState();
                }

                ShowImportResult(result);
            }

            summary = result.FatalError ??
                (result.Cancelled
                    ? $"导入已中断：新增 {result.CreatedPapers} 张，更新 {result.UpdatedPapers} 张，" +
                      $"失败 {result.FailedPapers} 张；已完成的部分已写入纸片。"
                    : $"导入完成：新增 {result.CreatedPapers} 张，更新 {result.UpdatedPapers} 张，" +
                      $"跳过 {result.SkippedPapers} 张，失败 {result.FailedPapers} 张。");
        }
        finally
        {
            _operation = null;
        }

        if (_disposed)
        {
            return;
        }

        // 导入完成后清空文件选择，避免误触重复导入。
        _importPlan = null;
        _importFilePath = null;
        _importSelection = null;
        InvalidatePreviewCache();
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
                evt =>
                {
                    // 笔记正文不在纸片/待办指纹里，单独标记以便下次刷新失效笔记预览。
                    if (evt is NoteChangedEvent)
                    {
                        _noteContentDirty = true;
                    }

                    if (evt is PaperDeletedEvent deleted)
                    {
                        _identity.RemoveByLocalPaperId(deleted.Paper.Id);
                        if (_identity.ConsumeChanges())
                        {
                            SaveState();
                        }
                    }

                    ScheduleRefresh();
                });
        }
        catch (PaperTodoPluginException)
        {
            // 订阅失败不影响导入正确性——每次刷新与导入前都会重新校验映射；
            // 但映射不会随纸片变更事件及时同步，需要在状态栏告知用户这一降级。
            var warning = "无法订阅纸片变更事件，导入映射将只在每次刷新与导入前重新校验。";
            _startupWarning = string.IsNullOrEmpty(_startupWarning)
                ? warning
                : _startupWarning + " " + warning;
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
            SetStatus($"操作被拒绝：{ex.Code} · {ex.Message}");
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

    private void ShowImportResult(ImportResult result)
    {
        var stats = new List<(string Label, string Value)>
        {
            ("新增纸片", $"{result.CreatedPapers} 张"),
            ("失败纸片", $"{result.FailedPapers} 张"),
            ("新增待办", $"{result.CreatedTodos} 条"),
            ("还原关联纸片", $"{result.RestoredLinks} 条"),
            ("还原提醒", $"{result.RestoredReminders} 条")
        };
        if (result.UpdatedPapers > 0)
        {
            stats.Insert(1, ("更新纸片", $"{result.UpdatedPapers} 张"));
        }

        if (result.SkippedPapers > 0)
        {
            stats.Insert(result.UpdatedPapers > 0 ? 2 : 1, ("跳过纸片", $"{result.SkippedPapers} 张"));
        }

        if (result.DeletedTodos > 0)
        {
            stats.Add(("删除待办", $"{result.DeletedTodos} 条"));
        }

        var lines = new List<string>();
        if (result.Cancelled)
        {
            lines.Add("导入被中断：已完成的部分已写入且不会回滚，请检查纸片内容后再继续。");
        }

        if (result.StoppedEarly && result.FatalError != null)
        {
            lines.Add(result.FatalError);
        }

        lines.AddRange(result.SkippedTitles.Select(title => "· 已跳过：" + title));
        lines.AddRange(result.DroppedPathLinkNotes.Select(note => "· " + note));
        lines.AddRange(result.Warnings.Select(warning => "· " + warning));
        lines.AddRange(result.Failures
            .Select(failure => $"· 失败：{failure.Title}（{failure.Code}）：{failure.Message}"));

        // 结果对话框本身可滚动；只有在极端情况下才截断，并明确告知省略了多少条。
        const int maxDetailLines = 300;
        if (lines.Count > maxDetailLines)
        {
            var omitted = lines.Count - maxDetailLines;
            lines = lines.Take(maxDetailLines).ToList();
            lines.Add($"· …另有 {omitted} 条详情未显示（可缩小选择范围后重试以查看）。");
        }

        var title = result.Cancelled
            ? "导入已中断（部分完成）"
            : result.StoppedEarly
                ? "部分导入完成"
                : result.FailedPapers > 0
                    ? "导入完成，有失败项"
                    : "导入完成";

        _view.ShowResultDialog(
            title,
            stats,
            lines.Count > 0 ? lines : null);
    }

    private static DataIoExportFormat ParseExportFormat(string? value) =>
        string.Equals(value, "markdown", StringComparison.OrdinalIgnoreCase)
            ? DataIoExportFormat.Markdown
            : DataIoExportFormat.Json;

    /// <summary>
    /// 插件状态损坏时把原始 JSON 备份到本机，并返回给用户看的提示。
    /// 状态里只有 UI 偏好与导入映射，不包含笔记正文，备份不涉及敏感内容。
    /// </summary>
    private static string BackupCorruptState(string? rawState)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PaperTodo",
                "DataIO",
                "state-backup");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(
                directory,
                $"state-corrupt-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.WriteAllText(path, rawState ?? "", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return $"插件状态已损坏并重置，原状态备份在 {path}；此前的导入关联可能丢失。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return "插件状态已损坏并重置；此前的导入关联可能丢失。";
        }
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
        "data_reload_in_progress" => "正在重载数据，请稍后重试。",
        _ => $"读取失败（{ex.Code}）：{ex.Message}"
    };
}
