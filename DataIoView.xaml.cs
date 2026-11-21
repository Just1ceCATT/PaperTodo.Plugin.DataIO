using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PaperTodo.Plugin;

namespace PaperTodo.Plugin.DataIo;

/// <summary>
/// 工具纸片主体界面：导出/导入双模式、内容类型入口与详情多选。
/// 只负责展示和收集用户输入，不直接调用宿主写 API。
/// </summary>
public partial class DataIoView : UserControl
{
    private readonly ObservableCollection<PaperRowItem> _rows = [];
    private readonly List<PaperRowItem> _allRows = [];
    private bool _suppressModeEvents;
    private bool _suppressSelectionEvents;
    private string _detailType = "note";
    private bool _busy;
    private bool _primaryEnabled;
    private bool _canExport = true;
    private bool _canImport = true;
    private PaperBodyTheme? _theme;
    private bool _sizeHooked;

    public DataIoMode Mode { get; private set; } = DataIoMode.Export;
    public bool IsDetailVisible => DetailPanel.Visibility == Visibility.Visible;
    public string CurrentSearch => SearchBox.Text?.Trim() ?? "";

    public event EventHandler? ModeChanged;
    public event EventHandler? NotesDetailRequested;
    public event EventHandler? TodosDetailRequested;
    public event EventHandler? BackRequested;
    public event EventHandler? PrimaryActionRequested;
    public event EventHandler? ChooseImportFileRequested;
    public event EventHandler? DetailSelectionChanged;
    public event EventHandler? SearchChanged;

    public DataIoView()
    {
        InitializeComponent();

        // 主题尚未注入时先提供常规密度，避免首帧出现零边距。
        Resources["DataIoRowPadding"] = new Thickness(12, 10, 12, 10);
        Resources["DataIoRowMargin"] = new Thickness(0, 6, 0, 0);
        Resources["DataIoDividerMargin"] = new Thickness(0, 18, 0, 4);
        Resources["DataIoPrimaryHeight"] = 46.0;

        // 模式卡片移入滚动区域，使其随内容一起上下滚动，而不是固定在顶部。
        if (ModeCardsGrid.Parent is Panel parent)
        {
            parent.Children.Remove(ModeCardsGrid);
        }

        ModeCardsGrid.Margin = new Thickness(0);
        SummaryPanel.Children.Insert(0, ModeCardsGrid);

        PaperList.ItemsSource = _rows;
        WireEvents();
        ApplyModeVisuals(DataIoMode.Export);
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
        ContentSectionHint.Text = isExport
            ? "点击条目选择要导出的具体内容"
            : "点击条目选择要导入的具体内容";
        FormatRow.Visibility = isExport ? Visibility.Visible : Visibility.Collapsed;
        StrategyRow.Visibility = isExport ? Visibility.Collapsed : Visibility.Visible;
        EstimateTitle.Text = isExport ? "导出预估" : "导入预估";
        PrimaryButton.Content = isExport ? "导出所选内容" : "导入所选内容";

        ShowDetail(false);
    }

    // ---------- 内容统计 ----------

    public void SetCounts(string notesText, string todosText)
    {
        NotesCountText.Text = notesText;
        TodosCountText.Text = todosText;
    }

    public void SetTypeAvailability(bool notesEnabled, bool todosEnabled)
    {
        NotesRow.IsEnabled = notesEnabled;
        NotesRow.Opacity = notesEnabled ? 1.0 : 0.45;
        TodosRow.IsEnabled = todosEnabled;
        TodosRow.Opacity = todosEnabled ? 1.0 : 0.45;
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

    public void SetDetailType(string type, string title)
    {
        _detailType = type;
        DetailTitle.Text = title;
        ApplyFilter();
    }

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

    public void SetSearch(string? search)
    {
        SearchBox.Text = search ?? "";
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

    private void ApplyFilter()
    {
        var search = CurrentSearch;

        _rows.Clear();
        foreach (var row in _allRows)
        {
            if (row.Type != _detailType)
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

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        ApplyFilter();
        SearchChanged?.Invoke(this, EventArgs.Empty);
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

        NotesRow.MouseLeftButtonUp += (_, _) => NotesDetailRequested?.Invoke(this, EventArgs.Empty);
        TodosRow.MouseLeftButtonUp += (_, _) => TodosDetailRequested?.Invoke(this, EventArgs.Empty);
        BackButton.Click += (_, _) => BackRequested?.Invoke(this, EventArgs.Empty);
        PrimaryButton.Click += (_, _) => PrimaryActionRequested?.Invoke(this, EventArgs.Empty);
        ChooseFileButton.Click += (_, _) => ChooseImportFileRequested?.Invoke(this, EventArgs.Empty);

        SelectAllCheckBox.Click += OnSelectAllClick;
        InvertButton.Click += OnInvertClick;
        SearchBox.TextChanged += OnSearchChanged;
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
