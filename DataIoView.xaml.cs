using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PaperTodo.Plugin;

namespace PaperTodo.Plugin.DataIo;

/// <summary>
/// 工具纸片主体界面：导出/导入双模式、类型选择、范围切换与详情勾选。
/// 只负责展示和收集用户输入，不直接调用宿主写 API。
/// </summary>
public partial class DataIoView : UserControl
{
    private readonly ObservableCollection<PaperRowItem> _rows = [];
    private readonly List<PaperRowItem> _allRows = [];
    private bool _suppressFilterEvents;
    private bool _suppressModeEvents;
    private bool _suppressToggleEvents;
    private bool _suppressSelectionEvents;
    private bool _busy;
    private bool _primaryEnabled;
    private bool _canExport = true;
    private bool _canImport = true;

    public DataIoMode Mode { get; private set; } = DataIoMode.Export;
    public DataIoScope Scope { get; private set; } = DataIoScope.All;
    public bool IncludeNotes => NotesToggle.IsChecked == true;
    public bool IncludeTodos => TodosToggle.IsChecked == true;
    public bool IsDetailVisible => DetailPanel.Visibility == Visibility.Visible;
    public string CurrentFilter =>
        (FilterBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "all";
    public string CurrentSearch => SearchBox.Text?.Trim() ?? "";

    public event EventHandler? ModeChanged;
    public event EventHandler? TypeSelectionChanged;
    public event EventHandler? ScopeAllRequested;
    public event EventHandler? ScopeCustomRequested;
    public event EventHandler? BackRequested;
    public event EventHandler? PrimaryActionRequested;
    public event EventHandler? ChooseImportFileRequested;
    public event EventHandler? DetailSelectionChanged;
    public event EventHandler? FilterChanged;

    public DataIoView()
    {
        InitializeComponent();
        PaperList.ItemsSource = _rows;
        BuildFilterItems();
        WireEvents();
        ApplyModeVisuals(DataIoMode.Export);
        SetScope(DataIoScope.All, 0);
        ApplyFilter();
    }

    // ---------- 模式 ----------

    public void SetMode(DataIoMode mode) => ApplyModeVisuals(mode);

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
        ContentSectionHint.Text = isExport ? "选择需要导出的数据类型" : "选择需要导入的数据类型";
        FormatRow.Visibility = isExport ? Visibility.Visible : Visibility.Collapsed;
        StrategyRow.Visibility = isExport ? Visibility.Collapsed : Visibility.Visible;
        EstimateTitle.Text = isExport ? "导出预估" : "导入预估";
        PrimaryButton.Content = isExport ? "导出所选内容" : "导入所选内容";

        ShowDetail(false);
    }

    // ---------- 内容类型 ----------

    public void SetTypeToggles(bool notes, bool todos)
    {
        _suppressToggleEvents = true;
        NotesToggle.IsChecked = notes;
        TodosToggle.IsChecked = todos;
        _suppressToggleEvents = false;
    }

    public void SetTypeAvailability(bool notesEnabled, bool todosEnabled)
    {
        NotesToggle.IsEnabled = notesEnabled;
        NotesRow.Opacity = notesEnabled ? 1.0 : 0.5;
        TodosToggle.IsEnabled = todosEnabled;
        TodosRow.Opacity = todosEnabled ? 1.0 : 0.5;
    }

    public void SetCounts(int notes, int todos)
    {
        NotesCountText.Text = $"{Math.Max(0, notes)} 条";
        TodosCountText.Text = $"{Math.Max(0, todos)} 条";
    }

    private void OnTypeToggled(object? sender, RoutedEventArgs e)
    {
        if (!_suppressToggleEvents)
        {
            TypeSelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    // ---------- 范围 ----------

    public void SetScope(DataIoScope scope, int selectedCount)
    {
        Scope = scope;
        ScopeAllBadge.Visibility = scope == DataIoScope.All ? Visibility.Visible : Visibility.Collapsed;
        ScopeCustomHint.Text = scope == DataIoScope.Custom ? $"已选 {Math.Max(0, selectedCount)} 项" : "";

        // 主题资源在构造函数之后才注入，这里用 TryFindResource 避免早期 KeyNotFound。
        var accentBorder = TryFindResource("DataIoAccentBorderBrush") as Brush ?? Brushes.Transparent;
        ScopeAllRow.BorderBrush = scope == DataIoScope.All ? accentBorder : Brushes.Transparent;
        ScopeCustomRow.BorderBrush = scope == DataIoScope.Custom ? accentBorder : Brushes.Transparent;
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

    public void SetImportFileName(string? fileName)
    {
        var hasFile = !string.IsNullOrWhiteSpace(fileName);
        ImportFileNameText.Text = hasFile ? fileName! : "尚未选择文件";
        ChooseFileButton.Content = hasFile ? "重新选择" : "选择文件";
    }

    public void SetBusy(bool busy)
    {
        _busy = busy;
        BusyBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        PrimaryButton.IsEnabled = !busy && _primaryEnabled;
        ChooseFileButton.IsEnabled = !busy;
        SelectAllCheckBox.IsEnabled = !busy;
        InvertButton.IsEnabled = !busy;
        ExportModeButton.IsEnabled = !busy && _canExport;
        ImportModeButton.IsEnabled = !busy && _canImport;
    }

    public void SetAvailability(bool canExport, bool canImport)
    {
        _canExport = canExport;
        _canImport = canImport;
        ExportModeButton.IsEnabled = !_busy && canExport;
        ImportModeButton.IsEnabled = !_busy && canImport;
    }

    // ---------- 详情 ----------

    public void ShowDetail(bool show)
    {
        SummaryScroll.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        DetailPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    public void SetDetailTitle(string title) => DetailTitle.Text = title;

    /// <summary>刷新详情列表；按 Key 保留已有勾选状态。</summary>
    public void SetRows(IReadOnlyList<PaperRowItem> rows)
    {
        _suppressSelectionEvents = true;

        var selected = new HashSet<string>(
            _allRows.Where(r => r.IsSelected).Select(r => r.Key),
            StringComparer.Ordinal);

        foreach (var oldRow in _allRows)
        {
            oldRow.PropertyChanged -= OnRowPropertyChanged;
        }

        _allRows.Clear();
        foreach (var row in rows)
        {
            row.IsSelected = selected.Contains(row.Key);
            row.PropertyChanged += OnRowPropertyChanged;
            _allRows.Add(row);
        }

        _suppressSelectionEvents = false;
        ApplyFilter();
    }

    public IReadOnlyList<string> GetSelectedKeys() =>
        _allRows
            .Where(r => r.IsSelected && r.CanSelect)
            .Select(r => r.Key)
            .ToList();

    public void SetSelection(IEnumerable<string> keys)
    {
        _suppressSelectionEvents = true;
        var set = new HashSet<string>(keys, StringComparer.Ordinal);
        foreach (var row in _allRows)
        {
            row.IsSelected = set.Contains(row.Key);
        }

        _suppressSelectionEvents = false;
        UpdateSelectAllState();
        DetailSelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SelectAllVisible()
    {
        _suppressSelectionEvents = true;
        foreach (var row in _rows.Where(r => r.CanSelect))
        {
            row.IsSelected = true;
        }

        _suppressSelectionEvents = false;
        UpdateSelectAllState();
        DetailSelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>忽略当前筛选，选中全部可选项；用于首次进入"指定"范围。</summary>
    public void SelectAllRows()
    {
        _suppressSelectionEvents = true;
        foreach (var row in _allRows.Where(r => r.CanSelect))
        {
            row.IsSelected = true;
        }

        _suppressSelectionEvents = false;
        UpdateSelectAllState();
        DetailSelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressSelectionEvents || e.PropertyName != nameof(PaperRowItem.IsSelected))
        {
            return;
        }

        UpdateSelectAllState();
        DetailSelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---------- 筛选 ----------

    public void SetFilter(string filter, string search)
    {
        _suppressFilterEvents = true;
        FilterBox.SelectedIndex = filter switch
        {
            "todo" => 1,
            "note" => 2,
            _ => 0
        };
        SearchBox.Text = search ?? "";
        _suppressFilterEvents = false;
        ApplyFilter();
    }

    private void BuildFilterItems()
    {
        _suppressFilterEvents = true;
        FilterBox.Items.Clear();
        FilterBox.Items.Add(new ComboBoxItem { Content = "全部", Tag = "all" });
        FilterBox.Items.Add(new ComboBoxItem { Content = "待办", Tag = "todo" });
        FilterBox.Items.Add(new ComboBoxItem { Content = "笔记", Tag = "note" });
        FilterBox.SelectedIndex = 0;
        _suppressFilterEvents = false;
    }

    private void ApplyFilter()
    {
        var filter = CurrentFilter;
        var search = CurrentSearch;

        _rows.Clear();
        foreach (var row in _allRows)
        {
            if (filter == "todo" && row.Type != "todo")
            {
                continue;
            }

            if (filter == "note" && row.Type != "note")
            {
                continue;
            }

            if (search.Length > 0 &&
                !row.Title.Contains(search, StringComparison.CurrentCultureIgnoreCase))
            {
                continue;
            }

            _rows.Add(row);
        }

        UpdateSelectAllState();
    }

    private void UpdateSelectAllState()
    {
        var selectable = _rows.Where(r => r.CanSelect).ToList();
        SelectAllCheckBox.IsChecked = selectable.Count > 0 && selectable.All(r => r.IsSelected);
    }

    private void OnFilterOrSearchChanged(object? sender, EventArgs e)
    {
        if (_suppressFilterEvents)
        {
            return;
        }

        ApplyFilter();
        FilterChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnSelectAllClick(object sender, RoutedEventArgs e)
    {
        var value = SelectAllCheckBox.IsChecked == true;
        _suppressSelectionEvents = true;
        foreach (var row in _rows.Where(r => r.CanSelect))
        {
            row.IsSelected = value;
        }

        _suppressSelectionEvents = false;
        SelectAllCheckBox.IsChecked = value;
        DetailSelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnInvertClick(object sender, RoutedEventArgs e)
    {
        _suppressSelectionEvents = true;
        foreach (var row in _rows.Where(r => r.CanSelect))
        {
            row.IsSelected = !row.IsSelected;
        }

        _suppressSelectionEvents = false;
        UpdateSelectAllState();
        DetailSelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void WireEvents()
    {
        ExportModeButton.Checked += OnModeChecked;
        ImportModeButton.Checked += OnModeChecked;

        NotesToggle.Checked += OnTypeToggled;
        NotesToggle.Unchecked += OnTypeToggled;
        TodosToggle.Checked += OnTypeToggled;
        TodosToggle.Unchecked += OnTypeToggled;

        ScopeAllRow.MouseLeftButtonUp += (_, _) => ScopeAllRequested?.Invoke(this, EventArgs.Empty);
        ScopeCustomRow.MouseLeftButtonUp += (_, _) => ScopeCustomRequested?.Invoke(this, EventArgs.Empty);
        BackButton.Click += (_, _) => BackRequested?.Invoke(this, EventArgs.Empty);
        PrimaryButton.Click += (_, _) => PrimaryActionRequested?.Invoke(this, EventArgs.Empty);
        ChooseFileButton.Click += (_, _) => ChooseImportFileRequested?.Invoke(this, EventArgs.Empty);

        SelectAllCheckBox.Click += OnSelectAllClick;
        InvertButton.Click += OnInvertClick;
        FilterBox.SelectionChanged += OnFilterOrSearchChanged;
        SearchBox.TextChanged += OnFilterOrSearchChanged;
    }

    // ---------- 主题 ----------

    public void ApplyTheme(PaperBodyTheme theme, IPaperBodyControls? controls = null)
    {
        var scale = theme.FontScale <= 0 ? 1.0 : Math.Clamp(theme.FontScale, 0.7, 2.5);

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
        Resources["DataIoFontSize"] = 12.5 * scale;
        Resources["DataIoTitleFontSize"] = 19.0 * scale;
        Resources["DataIoSectionFontSize"] = 13.5 * scale;
        Resources["DataIoEstimateFontSize"] = 16.0 * scale;
        Resources["DataIoWeakFontSize"] = 10.5 * scale;

        FontFamily = fontFamily;

        if (controls != null)
        {
            try
            {
                controls.ApplySelectStyle(FilterBox, 11.5 * scale);
            }
            catch (Exception)
            {
                // 宿主控件样式不可用时保持默认外观。
            }
        }
    }

    public void SetStatus(string text) => StatusText.Text = text;

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
