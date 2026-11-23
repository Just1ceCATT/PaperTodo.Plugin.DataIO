using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using PaperTodo.Plugin;

namespace PaperTodo.Plugin.DataIo;

/// <summary>
/// 工具纸片主体界面：导出/导入双模式、点击内容类型就地展开并多选。
/// 只负责展示和收集用户输入，不直接调用宿主写 API。
/// </summary>
public partial class DataIoView : UserControl
{
    private readonly ObservableCollection<PaperRowItem> _noteRows = [];
    private readonly ObservableCollection<PaperRowItem> _todoRows = [];
    private readonly List<PaperRowItem> _allRows = [];
    private readonly DispatcherTimer _hoverTimer;
    private PaperRowItem? _hoverRow;
    private int _hoverToken;
    private int _hoverCardToken;
    private bool _previewOpen;
    private bool _suppressModeEvents;
    private bool _suppressFormatEvents;
    private bool _suppressSelectionEvents;
    private bool _suppressSearchEvents;
    private bool _notesExpanded;
    private bool _todosExpanded;
    private int _notesAnimToken;
    private int _todosAnimToken;
    private bool _busy;
    private bool _primaryEnabled;
    private bool _canExport = true;
    private bool _canImport = true;
    private bool _notesAvailable = true;
    private bool _todosAvailable = true;
    private PaperBodyTheme? _theme;
    private bool _sizeHooked;
    private Window? _hostWindow;

    /// <summary>上一次单击行时切换了勾选的键；双击时用于抵消第一击的切换。</summary>
    private string? _pendingRowToggleKey;

    private const string ContentSectionHintText =
        "点击条目展开并选择；单击行切换勾选，双击行查看正文";

    public DataIoMode Mode { get; private set; } = DataIoMode.Export;

    /// <summary>当前选择的导出格式，由“导出格式”胶囊切换。</summary>
    public DataIoExportFormat ExportFormat { get; private set; } = DataIoExportFormat.Json;

    /// <summary>
    /// 由会话注入的按需读取器：导出模式经宿主 API 读取，导入模式读取文件内容。
    /// 视图不直接访问宿主数据，预览与悬停卡片都只消费这个委托。
    /// </summary>
    internal Func<string, Task<PreviewContent?>>? PreviewLoader { get; set; }

    public event EventHandler? ModeChanged;
    public event EventHandler? ExportFormatChanged;
    public event EventHandler? NoteSelectionChanged;
    public event EventHandler? TodoSelectionChanged;
    public event EventHandler? SearchChanged;
    public event EventHandler? PrimaryActionRequested;
    public event EventHandler? ChooseImportFileRequested;

    /// <summary>当前搜索框内容；会话据此过滤列表。</summary>
    public string SearchText => SearchBox.Text;

    public DataIoView()
    {
        InitializeComponent();

        // 主题尚未注入时先提供常规密度，避免首帧出现零边距。
        Resources["DataIoRowPadding"] = new Thickness(12, 10, 12, 10);
        Resources["DataIoRowMargin"] = new Thickness(0, 6, 0, 0);
        Resources["DataIoDividerMargin"] = new Thickness(0, 18, 0, 4);
        Resources["DataIoPrimaryHeight"] = 46.0;
        Resources["DataIoCheckSize"] = 16.0;
        Resources["DataIoCheckBorderThickness"] = new Thickness(1.5);
        Resources["DataIoCheckCornerRadius"] = new CornerRadius(4);
        Resources["DataIoCheckScale"] = 1.0;

        // 模式卡片移入滚动区域，使其随内容一起上下滚动，而不是固定在顶部。
        if (ModeCardsGrid.Parent is Panel parent)
        {
            parent.Children.Remove(ModeCardsGrid);
        }

        ModeCardsGrid.Margin = new Thickness(0);
        SummaryPanel.Children.Insert(0, ModeCardsGrid);

        NotesList.ItemsSource = _noteRows;
        TodosList.ItemsSource = _todoRows;

        _hoverTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(450)
        };
        _hoverTimer.Tick += OnHoverTimerTick;

        SummaryScroll.ScrollChanged += (_, _) => CancelHover();
        Loaded += OnViewLoaded;
        Unloaded += OnViewUnloaded;

        WireEvents();
        ApplyModeVisuals(DataIoMode.Export);
        ApplyExpansionImmediate();
    }

    // ---------- 模式 ----------

    public void SetMode(DataIoMode mode) => ApplyModeVisuals(mode);

    public void SetExportFormat(DataIoExportFormat format)
    {
        ExportFormat = format;
        _suppressFormatEvents = true;
        JsonFormatButton.IsChecked = format == DataIoExportFormat.Json;
        MarkdownFormatButton.IsChecked = format == DataIoExportFormat.Markdown;
        _suppressFormatEvents = false;
    }

    /// <summary>恢复上次的搜索关键字；不触发 SearchChanged。</summary>
    public void SetSearch(string? text)
    {
        _suppressSearchEvents = true;
        SearchBox.Text = text ?? "";
        _suppressSearchEvents = false;
        UpdateSearchVisuals();
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateSearchVisuals();
        if (!_suppressSearchEvents)
        {
            SearchChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnSearchClearClick(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = "";
        SearchBox.Focus();
    }

    private void UpdateSearchVisuals()
    {
        var hasText = !string.IsNullOrEmpty(SearchBox.Text);
        SearchPlaceholder.Visibility = hasText ? Visibility.Collapsed : Visibility.Visible;
        SearchClearButton.Visibility = hasText ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnExportFormatChecked(object? sender, RoutedEventArgs e)
    {
        if (_suppressFormatEvents)
        {
            return;
        }

        var format = ReferenceEquals(sender, MarkdownFormatButton)
            ? DataIoExportFormat.Markdown
            : DataIoExportFormat.Json;
        if (format == ExportFormat)
        {
            return;
        }

        ExportFormat = format;
        ExportFormatChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnModeChecked(object? sender, RoutedEventArgs e)
    {
        if (_suppressModeEvents)
        {
            return;
        }

        var mode = ReferenceEquals(sender, ImportModeButton) ? DataIoMode.Import : DataIoMode.Export;
        if (mode == Mode)
        {
            return;
        }

        ApplyModeVisuals(mode);
        PrimaryButton.IsEnabled = false;
        ModeChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyModeVisuals(DataIoMode mode)
    {
        Mode = mode;
        _suppressModeEvents = true;
        ExportModeButton.IsChecked = mode == DataIoMode.Export;
        ImportModeButton.IsChecked = mode == DataIoMode.Import;
        _suppressModeEvents = false;

        var isExport = mode == DataIoMode.Export;
        ImportFileSection.Visibility = isExport ? Visibility.Collapsed : Visibility.Visible;
        ContentSectionTitle.Text = isExport ? "导出内容" : "导入内容";
        ContentSectionHint.Text = ContentSectionHintText;
        FormatRow.Visibility = isExport ? Visibility.Visible : Visibility.Collapsed;
        EstimateTitle.Text = isExport ? "导出预估" : "导入预估";
        PrimaryButton.Content = isExport ? "导出所选内容" : "导入所选内容";

        _notesExpanded = false;
        _todosExpanded = false;
        CancelHover();
        ApplyExpansionImmediate();
    }

    // ---------- 展开 / 收起 ----------

    private void OnNotesRowClick(object sender, MouseButtonEventArgs e)
    {
        _notesExpanded = !_notesExpanded;
        // 只动画当前点击的面板，避免另一项被重复播放动画。
        AnimateNotes();
    }

    private void OnTodosRowClick(object sender, MouseButtonEventArgs e)
    {
        _todosExpanded = !_todosExpanded;
        AnimateTodos();
    }

    private void AnimateNotes() =>
        AnimatePanel(
            NotesPanel,
            NotesContent,
            NotesChevronRotate,
            _notesExpanded,
            ++_notesAnimToken);

    private void AnimateTodos() =>
        AnimatePanel(
            TodosPanel,
            TodosContent,
            TodosChevronRotate,
            _todosExpanded,
            ++_todosAnimToken);

    /// <summary>无动画地同步两个面板状态，用于初始化与模式切换。</summary>
    private void ApplyExpansionImmediate()
    {
        ApplyPanelState(NotesPanel, NotesChevronRotate, _notesExpanded, ref _notesAnimToken);
        ApplyPanelState(TodosPanel, TodosChevronRotate, _todosExpanded, ref _todosAnimToken);
    }

    private static void ApplyPanelState(
        Border panel,
        RotateTransform rotate,
        bool expand,
        ref int token)
    {
        // 让仍在播放的旧动画失效，并清除动画对属性的占用。
        token++;
        panel.Tag = token;
        panel.BeginAnimation(HeightProperty, null);
        panel.BeginAnimation(OpacityProperty, null);
        rotate.BeginAnimation(RotateTransform.AngleProperty, null);

        panel.Opacity = 1;
        if (expand)
        {
            panel.Height = double.NaN;
            panel.Visibility = Visibility.Visible;
            rotate.Angle = 90;
        }
        else
        {
            panel.Height = 0;
            panel.Visibility = Visibility.Collapsed;
            rotate.Angle = 0;
        }
    }

    /// <summary>高度 + 透明度 + 箭头旋转的组合动画，展开和收起都平滑过渡。</summary>
    private void AnimatePanel(
        Border panel,
        FrameworkElement content,
        RotateTransform rotate,
        bool expand,
        int token)
    {
        // 用 Tag 记录本次动画序号，避免快速连点时旧动画的回调覆盖新状态。
        panel.Tag = token;

        double from;
        if (expand)
        {
            from = 0;
            panel.Height = 0;
        }
        else
        {
            from = panel.ActualHeight > 0 ? panel.ActualHeight : 0;
            panel.Height = from;
        }

        // 先显示以便测量内容的自然高度。
        panel.Visibility = Visibility.Visible;

        var width = panel.ActualWidth > 0
            ? panel.ActualWidth
            : Math.Max(0, ActualWidth - 40);
        content.Measure(new Size(width, double.PositiveInfinity));
        var target = expand ? content.DesiredSize.Height : 0;

        var heightAnimation = new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd
        };
        heightAnimation.Completed += (_, _) =>
        {
            if (panel.Tag is int activeToken && activeToken != token)
            {
                return;
            }

            panel.BeginAnimation(HeightProperty, null);
            if (expand)
            {
                // 恢复自适应高度，列表刷新时能随内容增减。
                panel.Height = double.NaN;
            }
            else
            {
                panel.Height = 0;
                panel.Visibility = Visibility.Collapsed;
            }

            panel.Opacity = 1;
        };
        panel.BeginAnimation(HeightProperty, heightAnimation);

        var opacityAnimation = new DoubleAnimation(
            expand ? 0 : 1,
            expand ? 1 : 0,
            TimeSpan.FromMilliseconds(160))
        {
            FillBehavior = FillBehavior.HoldEnd
        };
        panel.BeginAnimation(OpacityProperty, opacityAnimation);

        var rotateAnimation = new DoubleAnimation(
            expand ? 0 : 90,
            expand ? 90 : 0,
            TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd
        };
        rotate.BeginAnimation(RotateTransform.AngleProperty, rotateAnimation);
    }

    // ---------- 内容统计 ----------

    public void SetCounts(string notesText, string todosText)
    {
        NotesCountText.Text = notesText;
        TodosCountText.Text = todosText;
    }

    public void SetTypeAvailability(bool notesEnabled, bool todosEnabled)
    {
        _notesAvailable = notesEnabled;
        _todosAvailable = todosEnabled;
        ApplyRowAvailability();

        // 只在对应项被禁用且原本展开时收起它，不影响另一项。
        if (!notesEnabled && _notesExpanded)
        {
            _notesExpanded = false;
            AnimateNotes();
        }

        if (!todosEnabled && _todosExpanded)
        {
            _todosExpanded = false;
            AnimateTodos();
        }
    }

    // ---------- 预估与主操作 ----------

    public void SetEstimate(string main, string sub)
    {
        EstimateMain.Text = main;
        EstimateSub.Text = sub;
    }

    public void SetPrimaryEnabled(bool enabled)
    {
        _primaryEnabled = enabled;
        PrimaryButton.IsEnabled = !_busy && enabled;
    }

    /// <summary>
    /// 更新导入文件信息行。status 为 null 时展示默认的格式支持说明；
    /// 选择文件后改为展示解析状态，避免重复说明。
    /// </summary>
    public void SetImportFileInfo(string? fileName, string? status = null)
    {
        var hasFile = !string.IsNullOrWhiteSpace(fileName);
        ImportFileNameText.Text = hasFile ? fileName! : "尚未选择文件";
        ImportFileNameText.ToolTip = hasFile ? fileName : null;
        ImportFileStatusText.Text = string.IsNullOrWhiteSpace(status)
            ? "支持 PaperTodo JSON 或 Markdown 文档 (*.md)"
            : status!;
        ChooseFileButton.Content = hasFile ? "重新选择" : "选择文件";
    }

    /// <summary>导入文件含图片引用时的醒目提示；null 表示隐藏。</summary>
    public void SetImageWarning(string? text)
    {
        var hasText = !string.IsNullOrWhiteSpace(text);
        ImageWarningText.Text = hasText ? text! : "";
        ImageWarningText.Visibility = hasText ? Visibility.Visible : Visibility.Collapsed;
    }

    public void SetBusy(bool busy)
    {
        if (busy)
        {
            CancelHover();
        }

        _busy = busy;
        BusyBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        PrimaryButton.IsEnabled = !busy && _primaryEnabled;
        ChooseFileButton.IsEnabled = !busy;
        NotesSelectAllButton.IsEnabled = !busy;
        NotesClearButton.IsEnabled = !busy;
        TodosSelectAllButton.IsEnabled = !busy;
        TodosClearButton.IsEnabled = !busy;
        NotesInvertButton.IsEnabled = !busy;
        TodosInvertButton.IsEnabled = !busy;
        SearchBox.IsEnabled = !busy;
        SearchClearButton.IsEnabled = !busy;
        JsonFormatButton.IsEnabled = !busy;
        MarkdownFormatButton.IsEnabled = !busy;
        // 忙碌期间锁住行内选择与预览，避免出现被会话忽略的勾选操作。
        NotesList.IsEnabled = !busy;
        TodosList.IsEnabled = !busy;
        ExportModeButton.IsEnabled = !busy && _canExport;
        ImportModeButton.IsEnabled = !busy && _canImport;
        ApplyRowAvailability();
    }

    /// <summary>行本身是否可交互同时取决于数据可用性与忙碌状态。</summary>
    private void ApplyRowAvailability()
    {
        var notesInteractive = !_busy && _notesAvailable;
        var todosInteractive = !_busy && _todosAvailable;
        NotesRow.IsEnabled = notesInteractive;
        TodosRow.IsEnabled = todosInteractive;
        NotesRow.Opacity = notesInteractive ? 1.0 : 0.45;
        TodosRow.Opacity = todosInteractive ? 1.0 : 0.45;
    }

    public void SetAvailability(bool canExport, bool canImport)
    {
        _canExport = canExport;
        _canImport = canImport;
        ExportModeButton.IsEnabled = !_busy && canExport;
        ImportModeButton.IsEnabled = !_busy && canImport;
    }

    // ---------- 行与选择 ----------

    /// <summary>刷新两个类型的具体条目；selectedKeys 为 null 时默认全选可选项。</summary>
    public void SetRows(IReadOnlyList<PaperRowItem> rows, IReadOnlyCollection<string>? selectedKeys)
    {
        CancelHover();
        _suppressSelectionEvents = true;

        foreach (var oldRow in _allRows)
        {
            oldRow.PropertyChanged -= OnRowPropertyChanged;
        }

        _allRows.Clear();
        _noteRows.Clear();
        _todoRows.Clear();

        var selected = selectedKeys is null
            ? null
            : new HashSet<string>(selectedKeys, StringComparer.Ordinal);

        foreach (var row in rows)
        {
            row.IsSelected = selected is null ? row.CanSelect : selected.Contains(row.Key);
            row.PropertyChanged += OnRowPropertyChanged;
            _allRows.Add(row);

            if (row.Type == "todo")
            {
                // 每组第一行不画顶线，其余行画在顶边；必须在加入绑定集合前设置。
                row.ShowTopSeparator = _todoRows.Count > 0;
                _todoRows.Add(row);
            }
            else
            {
                row.ShowTopSeparator = _noteRows.Count > 0;
                _noteRows.Add(row);
            }
        }

        _suppressSelectionEvents = false;
    }

    public IReadOnlyList<string> GetSelectedKeys(string type) =>
        _allRows
            .Where(r => r.Type == type && r.IsSelected && r.CanSelect)
            .Select(r => r.Key)
            .ToList();

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressSelectionEvents || e.PropertyName != nameof(PaperRowItem.IsSelected))
        {
            return;
        }

        if (sender is not PaperRowItem row)
        {
            return;
        }

        RaiseSelectionChanged(row.Type);
    }

    private void RaiseSelectionChanged(string type)
    {
        if (type == "todo")
        {
            TodoSelectionChanged?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            NoteSelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    // 全选/清空是幂等动作，不依赖控件自身勾选状态。
    private void OnNotesSelectAllClick(object sender, RoutedEventArgs e) => SetAllOfType("note", true);

    private void OnNotesClearClick(object sender, RoutedEventArgs e) => SetAllOfType("note", false);

    private void OnTodosSelectAllClick(object sender, RoutedEventArgs e) => SetAllOfType("todo", true);

    private void OnTodosClearClick(object sender, RoutedEventArgs e) => SetAllOfType("todo", false);

    private void OnNotesInvertClick(object sender, RoutedEventArgs e) => InvertType("note");

    private void OnTodosInvertClick(object sender, RoutedEventArgs e) => InvertType("todo");

    private void SetAllOfType(string type, bool value)
    {
        var rows = type == "todo" ? _todoRows : _noteRows;
        _suppressSelectionEvents = true;
        foreach (var row in rows.Where(r => r.CanSelect))
        {
            row.IsSelected = value;
        }

        _suppressSelectionEvents = false;
        RaiseSelectionChanged(type);
    }

    private void InvertType(string type)
    {
        var rows = type == "todo" ? _todoRows : _noteRows;
        _suppressSelectionEvents = true;
        foreach (var row in rows.Where(r => r.CanSelect))
        {
            row.IsSelected = !row.IsSelected;
        }

        _suppressSelectionEvents = false;
        RaiseSelectionChanged(type);
    }

    private void WireEvents()
    {
        ExportModeButton.Checked += OnModeChecked;
        ImportModeButton.Checked += OnModeChecked;
        JsonFormatButton.Checked += OnExportFormatChecked;
        MarkdownFormatButton.Checked += OnExportFormatChecked;
        SearchBox.TextChanged += OnSearchTextChanged;
        SearchClearButton.Click += OnSearchClearClick;

        NotesRow.MouseLeftButtonUp += OnNotesRowClick;
        TodosRow.MouseLeftButtonUp += OnTodosRowClick;
        NotesSelectAllButton.Click += OnNotesSelectAllClick;
        NotesClearButton.Click += OnNotesClearClick;
        TodosSelectAllButton.Click += OnTodosSelectAllClick;
        TodosClearButton.Click += OnTodosClearClick;
        NotesInvertButton.Click += OnNotesInvertClick;
        TodosInvertButton.Click += OnTodosInvertClick;

        PrimaryButton.Click += (_, _) => PrimaryActionRequested?.Invoke(this, EventArgs.Empty);
        ChooseFileButton.Click += (_, _) => ChooseImportFileRequested?.Invoke(this, EventArgs.Empty);
    }

    // ---------- 预览 ----------

    /// <summary>行内预览按钮：打开完整预览窗口。</summary>
    private void OnPreviewRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PaperRowItem row })
        {
            OpenPreview(row);
        }

        e.Handled = true;
    }

    /// <summary>
    /// 行内点击：单击切换勾选，双击打开预览；点在复选框/预览按钮等交互控件上时不处理。
    /// </summary>
    private void OnRowMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PaperRowItem row })
        {
            return;
        }

        if (e.OriginalSource is DependencyObject source && IsInteractiveSource(source))
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            // 双击的第一击已切换过勾选；这里先抵消，保证双击只负责预览。
            if (_pendingRowToggleKey == row.Key)
            {
                if (row.CanSelect)
                {
                    row.IsSelected = !row.IsSelected;
                }

                _pendingRowToggleKey = null;
            }

            e.Handled = true;
            OpenPreview(row);
            return;
        }

        if (e.ClickCount == 1 && row.CanSelect)
        {
            row.IsSelected = !row.IsSelected;
            _pendingRowToggleKey = row.Key;
            e.Handled = true;
        }
    }

    /// <summary>笔记/待办分组行支持空格或回车展开、收起。</summary>
    private void OnTypeRowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.IsRepeat || e.Key is not (Key.Space or Key.Enter))
        {
            return;
        }

        if (ReferenceEquals(sender, NotesRow))
        {
            if (!NotesRow.IsEnabled)
            {
                return;
            }

            _notesExpanded = !_notesExpanded;
            AnimateNotes();
            e.Handled = true;
        }
        else if (ReferenceEquals(sender, TodosRow))
        {
            if (!TodosRow.IsEnabled)
            {
                return;
            }

            _todosExpanded = !_todosExpanded;
            AnimateTodos();
            e.Handled = true;
        }
    }

    private static bool IsInteractiveSource(DependencyObject source)
    {
        for (var current = source; current is not null; current = DependencyTree.GetParent(current))
        {
            if (current is ButtonBase or TextBoxBase)
            {
                return true;
            }
        }

        return false;
    }

    private void OpenPreview(PaperRowItem row)
    {
        if (_busy || _previewOpen || PreviewLoader is null)
        {
            return;
        }

        CancelHover();

        // 预览按界面顺序浏览：笔记组在前、待办组在后，与列表展开后的视觉顺序一致。
        var keys = _noteRows.Concat(_todoRows).Select(r => r.Key).ToList();
        var index = keys.IndexOf(row.Key);
        if (index < 0)
        {
            keys.Insert(0, row.Key);
            index = 0;
        }

        var window = new PaperPreviewWindow(
            CurrentTheme,
            ResolveTintColor(),
            Mode,
            keys,
            index,
            PreviewLoader,
            key => _allRows.FirstOrDefault(r => r.Key == key) is { CanSelect: true },
            key => _allRows.FirstOrDefault(r => r.Key == key)?.IsSelected ?? false,
            SetRowSelected);

        _previewOpen = true;
        try
        {
            window.ShowModal(Window.GetWindow(this));
        }
        finally
        {
            _previewOpen = false;
        }
    }

    private void SetRowSelected(string key, bool value)
    {
        var row = _allRows.FirstOrDefault(r => r.Key == key);
        if (row is { CanSelect: true })
        {
            row.IsSelected = value;
        }
    }

    // ---------- 悬停快速预览 ----------

    private void OnRowMouseEnter(object sender, MouseEventArgs e)
    {
        if (_busy || _previewOpen || PreviewLoader is null || e.StylusDevice != null)
        {
            return;
        }

        if (sender is not FrameworkElement { DataContext: PaperRowItem row })
        {
            return;
        }

        // 快速从一行移到另一行时，旧行的 MouseLeave 可能晚于新行的 MouseEnter 到达，
        // 用 _hoverRow 判断归属，避免误清空新行。
        _hoverTimer.Stop();
        HideHoverCard();
        _hoverRow = row;
        _hoverToken++;
        _hoverTimer.Start();
    }

    private void OnRowMouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PaperRowItem row } &&
            ReferenceEquals(_hoverRow, row))
        {
            CancelHover();
        }
    }

    /// <summary>鼠标移到复选框或按钮上时立即收起卡片，避免挡住点击目标。</summary>
    private void OnRowMouseMove(object sender, MouseEventArgs e)
    {
        if (HoverPreviewPopup.IsOpen &&
            ReferenceEquals(_hoverRow, (sender as FrameworkElement)?.DataContext) &&
            e.OriginalSource is DependencyObject source &&
            IsInteractiveSource(source))
        {
            CancelHover();
        }
    }

    private void OnViewLoaded(object sender, RoutedEventArgs e)
    {
        var window = Window.GetWindow(this);
        if (ReferenceEquals(window, _hostWindow))
        {
            return;
        }

        DetachHostWindow();
        _hostWindow = window;
        if (_hostWindow is not null)
        {
            // 宿主纸片失去焦点时收起悬停卡片，避免卡片残留到其他窗口上方。
            _hostWindow.Deactivated += OnHostWindowDeactivated;
        }
    }

    private void OnViewUnloaded(object sender, RoutedEventArgs e)
    {
        CancelHover();
        DetachHostWindow();
    }

    private void DetachHostWindow()
    {
        if (_hostWindow is null)
        {
            return;
        }

        _hostWindow.Deactivated -= OnHostWindowDeactivated;
        _hostWindow = null;
    }

    private void OnHostWindowDeactivated(object? sender, EventArgs e) => CancelHover();

    private void CancelHover()
    {
        _hoverTimer.Stop();
        _hoverRow = null;
        _hoverToken++;
        HideHoverCard();
    }

    /// <summary>淡出并延迟关闭悬停卡片；后续如果打开了新的卡片，旧动画不会误关新卡。</summary>
    private void HideHoverCard()
    {
        var token = ++_hoverCardToken;
        if (!HoverPreviewPopup.IsOpen)
        {
            return;
        }

        if (!AnimationsEnabled)
        {
            HoverPreviewPopup.IsOpen = false;
            HoverCard.Opacity = 1;
            HoverCardTranslate.Y = 0;
            return;
        }

        // 基础值设为终态，动画结束后回落到 0；下次打开前会重置为 1。
        HoverCard.Opacity = 0;
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(100))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
            FillBehavior = FillBehavior.Stop
        };
        fade.Completed += (_, _) =>
        {
            if (token != _hoverCardToken)
            {
                return;
            }

            HoverPreviewPopup.IsOpen = false;
            HoverCard.Opacity = 1;
            HoverCardTranslate.Y = 0;
        };

        HoverCard.BeginAnimation(OpacityProperty, fade);
        HoverCardTranslate.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(0, 4, TimeSpan.FromMilliseconds(100))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
                FillBehavior = FillBehavior.Stop
            });
    }

    private async void OnHoverTimerTick(object? sender, EventArgs e)
    {
        _hoverTimer.Stop();

        var row = _hoverRow;
        if (row is null || _busy || _previewOpen || PreviewLoader is null)
        {
            return;
        }

        // 鼠标停在复选框或预览按钮上时不弹卡片，避免干扰点击。
        if (Mouse.DirectlyOver is DependencyObject over && IsInteractiveSource(over))
        {
            return;
        }

        var token = ++_hoverToken;
        PreviewContent? content;
        try
        {
            content = await PreviewLoader(row.Key);
        }
        catch (Exception)
        {
            // 悬停预览失败时静默放弃，不打扰正在进行的勾选操作。
            return;
        }

        if (content is null ||
            token != _hoverToken ||
            !ReferenceEquals(_hoverRow, row) ||
            _busy ||
            _previewOpen)
        {
            return;
        }

        ShowHoverCard(content);
    }

    private void ShowHoverCard(PreviewContent content)
    {
        HoverTitle.Text = content.Title;
        HoverType.Text = content.TypeLabel;
        HoverMeta.Text = content.Meta;

        if (string.IsNullOrWhiteSpace(content.Warning))
        {
            HoverWarning.Visibility = Visibility.Collapsed;
        }
        else
        {
            HoverWarning.Text = content.Warning;
            HoverWarning.Visibility = Visibility.Visible;
        }

        SetHoverExcerpt(content);

        HoverPreviewPopup.Placement = PlacementMode.MousePoint;
        HoverPreviewPopup.HorizontalOffset = 16;
        HoverPreviewPopup.VerticalOffset = 18;

        ++_hoverCardToken;
        HoverCard.Opacity = 1;
        HoverCardTranslate.Y = 0;
        HoverPreviewPopup.IsOpen = true;

        if (!AnimationsEnabled)
        {
            return;
        }

        HoverCard.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            });
        HoverCardTranslate.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(6, 0, TimeSpan.FromMilliseconds(160))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            });
    }

    private static bool AnimationsEnabled => SystemParameters.ClientAreaAnimation;

    /// <summary>
    /// 悬停卡片摘要：待办使用与预览窗口一致的 Segoe Fluent Icons 勾选字形，
    /// 笔记则退化为轻量纯文本。
    /// </summary>
    private void SetHoverExcerpt(PreviewContent content)
    {
        HoverExcerpt.Inlines.Clear();

        if (content.Todos.Count > 0)
        {
            var iconFont = (FontFamily)Resources["DataIoIconFontFamily"];
            var shown = Math.Min(content.Todos.Count, 4);
            for (var i = 0; i < shown; i++)
            {
                if (i > 0)
                {
                    HoverExcerpt.Inlines.Add(new LineBreak());
                }

                var todo = content.Todos[i];
                HoverExcerpt.Inlines.Add(new Run(todo.Done ? "" : "")
                {
                    FontFamily = iconFont,
                    Foreground = (Brush)Resources[todo.Done ? "DataIoAccentBrush" : "DataIoWeakBrush"]
                });
                HoverExcerpt.Inlines.Add(new Run(" " + todo.Text));
            }

            if (content.Todos.Count > shown)
            {
                HoverExcerpt.Inlines.Add(new LineBreak());
                HoverExcerpt.Inlines.Add(new Run($"…还有 {content.Todos.Count - shown} 项")
                {
                    Foreground = (Brush)Resources["DataIoWeakBrush"]
                });
            }

            return;
        }

        HoverExcerpt.Text = content.IsTodo
            ? "暂无待办。"
            : BuildExcerpt(content);
    }

    private static string BuildExcerpt(PreviewContent content)
    {
        if (!content.ContentAvailable)
        {
            return "正文不可读取。";
        }

        var plain = MarkdownPreview.ToPlainText(content.Body);
        return plain.Length == 0 ? "（空白笔记）" : plain;
    }

    // ---------- 主题 ----------

    public void ApplyTheme(PaperBodyTheme theme, IPaperBodyControls? controls = null)
    {
        _theme = theme;

        Resources["DataIoTextBrush"] = MakeBrush(theme.TextColor, Color.FromRgb(0x20, 0x20, 0x20));
        Resources["DataIoWeakBrush"] = MakeBrush(theme.WeakTextColor, Color.FromRgb(0x70, 0x70, 0x70));
        Resources["DataIoBorderBrush"] = MakeBrush(theme.BorderColor, Color.FromRgb(0x80, 0x70, 0x50));
        Resources["DataIoAccentBrush"] = MakeBrush(theme.AccentColor, Color.FromRgb(0x2F, 0x6B, 0xFF));
        Resources["DataIoAccentBorderBrush"] = MakeBrushWithAlpha(
            theme.AccentColor, 130, Color.FromRgb(0x2F, 0x6B, 0xFF));
        Resources["DataIoOnAccentBrush"] = MakeBrush("#FFFFFFFF", Colors.White);
        Resources["DataIoSurfaceBrush"] = MakeBrush(
            theme.IsDark ? "#18FFFFFF" : "#0C000000",
            Color.FromArgb(0x0C, 0x00, 0x00, 0x00));
        // 悬停卡片位于透明浮层上，需要不透明底色，否则会和纸片背景叠成一片。
        Resources["DataIoCardBrush"] = MakeBrush(
            theme.PaperColor,
            theme.IsDark ? Color.FromRgb(0x21, 0x1F, 0x1C) : Color.FromRgb(0xFF, 0xF9, 0xEA));
        Resources["DataIoAccentSurfaceBrush"] = MakeBrushWithAlpha(
            theme.AccentColor,
            theme.IsDark ? (byte)46 : (byte)26,
            Color.FromRgb(0x2F, 0x6B, 0xFF));
        Resources["DataIoWarningBrush"] = MakeBrush(
            theme.IsDark ? "#FFD97B72" : "#FFC44B42",
            Color.FromRgb(0xC4, 0x4B, 0x42));
        Resources["DataIoDividerBrush"] = MakeBrush(
            theme.IsDark ? "#1FFFFFFF" : "#14000000",
            Color.FromArgb(0x14, 0x00, 0x00, 0x00));

        var fontFamily = new FontFamily(string.IsNullOrWhiteSpace(theme.FontFamily)
            ? "Microsoft YaHei UI"
            : theme.FontFamily);
        Resources["DataIoFontFamily"] = fontFamily;
        Resources["DataIoIconFontFamily"] = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
        FontFamily = fontFamily;

        if (!_sizeHooked)
        {
            SizeChanged += OnViewSizeChanged;
            _sizeHooked = true;
        }

        ApplyMetrics();
    }

    private void OnViewSizeChanged(object sender, SizeChangedEventArgs e) => ApplyMetrics();

    /// <summary>
    /// 根据纸片实际宽高切换紧凑布局：窄纸片上下排列模式卡片、隐藏副标题，
    /// 小纸片收紧边距与字号；大纸片保持常规密度。
    /// </summary>
    private void ApplyMetrics()
    {
        if (_theme is not { } theme)
        {
            return;
        }

        var width = ActualWidth;
        var height = ActualHeight;
        if (double.IsNaN(width) || width <= 0)
        {
            width = 320;
        }

        if (double.IsNaN(height) || height <= 0)
        {
            height = 360;
        }

        var narrow = width < 380;
        var stacked = width < 300;
        var compact = height < 560 || narrow;
        var hideSubtitle = narrow || height < 460;
        var tiny = width < 320;

        var themeScale = theme.FontScale <= 0 ? 1.0 : Math.Clamp(theme.FontScale, 0.7, 2.5);
        var widthFactor = Math.Clamp((width - 240) / 480.0, 0.84, 1.0);
        var scale = themeScale * widthFactor;

        Resources["DataIoFontSize"] = 12.5 * scale;
        Resources["DataIoTitleFontSize"] = (narrow ? 16.0 : 19.0) * scale;
        Resources["DataIoSectionFontSize"] = (narrow ? 12.5 : 13.5) * scale;
        Resources["DataIoEstimateFontSize"] = (narrow ? 14.0 : 16.0) * scale;
        Resources["DataIoWeakFontSize"] = (narrow ? 9.5 : 10.5) * scale;

        Resources["DataIoRowPadding"] = compact
            ? new Thickness(10, 8, 10, 8)
            : new Thickness(12, 10, 12, 10);
        Resources["DataIoRowMargin"] = new Thickness(0, compact ? 4 : 6, 0, 0);
        Resources["DataIoDividerMargin"] = new Thickness(0, compact ? 12 : 18, 0, 4);
        Resources["DataIoPrimaryHeight"] = compact ? 40.0 : 46.0;

        // 勾选框尺寸跟随宿主整体 UI 缩放（AppTypography.Scale），与原生待办勾选框一致。
        Resources["DataIoCheckSize"] = 16.0 * themeScale;
        Resources["DataIoCheckBorderThickness"] = new Thickness(1.5 * themeScale);
        Resources["DataIoCheckCornerRadius"] = new CornerRadius(4 * themeScale);
        Resources["DataIoCheckScale"] = themeScale;

        RootGrid.Margin = compact
            ? new Thickness(12, 10, 12, 10)
            : new Thickness(18, 16, 18, 14);
        ContentSectionTitle.Margin = new Thickness(0, compact ? 12 : 16, 0, 0);
        EstimateTitle.Margin = new Thickness(0, compact ? 12 : 18, 0, 0);

        HeaderBadge.Visibility = tiny ? Visibility.Collapsed : Visibility.Visible;
        ExportModeSubtitle.Visibility = hideSubtitle ? Visibility.Collapsed : Visibility.Visible;
        ImportModeSubtitle.Visibility = hideSubtitle ? Visibility.Collapsed : Visibility.Visible;
        SummaryPanel.Margin = compact ? new Thickness(0, 0, 10, 0) : new Thickness(0, 0, 4, 0);

        ModeCardsGrid.Columns = stacked ? 1 : 2;
        ModeCardsGrid.Margin = new Thickness(0);
        ExportModeButton.Padding = compact
            ? new Thickness(6, 8, 6, 8)
            : new Thickness(8, 10, 8, 10);
        ImportModeButton.Padding = ExportModeButton.Padding;
        ExportModeButton.Margin = stacked ? new Thickness(0, 0, 0, 8) : new Thickness(0, 0, 5, 0);
        ImportModeButton.Margin = stacked ? new Thickness(0) : new Thickness(5, 0, 0, 0);
        ImportFileSection.Margin = new Thickness(0, compact ? 12 : 16, 0, 0);
    }

    public void SetStatus(string text) => StatusText.Text = text;

    // ---------- 自定义弹窗 ----------

    private PaperBodyTheme CurrentTheme => _theme ?? new PaperBodyTheme(
        true,
        "#211F1C",
        "#E7E0D4",
        "#92897B",
        "#A88E6A",
        "#4C453D",
        "Microsoft YaHei UI",
        1.0);

    /// <summary>宿主 PaperNoticeDialog 使用的 Tint 颜色（从窗口资源 HoverBrushKey 提取）。</summary>
    private Color ResolveTintColor()
    {
        if (TryFindResource("HoverBrushKey") is SolidColorBrush hover)
        {
            return Color.FromRgb(hover.Color.R, hover.Color.G, hover.Color.B);
        }

        try
        {
            if (ColorConverter.ConvertFromString(CurrentTheme.AccentColor) is Color accent)
            {
                return accent;
            }
        }
        catch (FormatException)
        {
            // 回退到默认 Tint。
        }
        catch (NotSupportedException)
        {
            // 回退到默认 Tint。
        }

        return CurrentTheme.IsDark
            ? Color.FromRgb(120, 92, 48)
            : Color.FromRgb(230, 223, 211);
    }

    public bool ShowConfirmDialog(
        string title,
        string message,
        string confirmText = "继续",
        string cancelText = "取消",
        IReadOnlyList<string>? warnings = null)
    {
        var dialog = new DataIoDialog(
            CurrentTheme,
            ResolveTintColor(),
            title,
            confirmText,
            cancelText);
        dialog.SetMessage(message, warnings);
        return dialog.ShowModal(Window.GetWindow(this)) == true;
    }

    /// <summary>
    /// 展示更新差异并收集每张纸片的处理方式；unrecoverable 为文件层面无法完整还原的内容。
    /// 返回 null 表示取消导入。
    /// </summary>
    internal Dictionary<ImportPaper, ImportUpdateAction>? ShowUpdatePreviewDialog(
        IReadOnlyList<ImportPaper> papers,
        IReadOnlyList<string>? unrecoverable,
        bool canCreate,
        IReadOnlyList<string>? warnings = null)
    {
        var dialog = new DataIoDialog(
            CurrentTheme,
            ResolveTintColor(),
            "更新预览",
            "继续导入",
            "取消");
        dialog.SetUpdatePreview(papers, unrecoverable, canCreate, warnings);
        if (dialog.ShowModal(Window.GetWindow(this)) != true)
        {
            return null;
        }

        return new Dictionary<ImportPaper, ImportUpdateAction>(dialog.UpdateChoices);
    }

    public void ShowResultDialog(
        string title,
        IReadOnlyList<(string Label, string Value)> stats,
        IReadOnlyList<string>? details)
    {
        var dialog = new DataIoDialog(
            CurrentTheme,
            ResolveTintColor(),
            title,
            "完成");
        dialog.SetStats(stats, details);
        dialog.ShowModal(Window.GetWindow(this));
    }

    public void ShowAlertDialog(string title, string message)
    {
        var dialog = new DataIoDialog(
            CurrentTheme,
            ResolveTintColor(),
            title,
            "知道了");
        dialog.SetMessage(message);
        dialog.ShowModal(Window.GetWindow(this));
    }

    private static SolidColorBrush MakeBrush(string? hex, Color fallback)
    {
        var brush = new SolidColorBrush(ParseColor(hex, fallback));
        brush.Freeze();
        return brush;
    }

    private static SolidColorBrush MakeBrushWithAlpha(string? hex, byte alpha, Color fallback)
    {
        var color = ParseColor(hex, fallback);
        var brush = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }

    private static Color ParseColor(string? hex, Color fallback)
    {
        if (!string.IsNullOrWhiteSpace(hex))
        {
            try
            {
                if (ColorConverter.ConvertFromString(hex) is Color color)
                {
                    return color;
                }
            }
            catch (FormatException)
            {
                // 回退到默认颜色。
            }
            catch (NotSupportedException)
            {
                // 回退到默认颜色。
            }
        }

        return fallback;
    }
}
