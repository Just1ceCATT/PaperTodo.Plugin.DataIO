using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
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
    private bool _suppressModeEvents;
    private bool _suppressSelectionEvents;
    private bool _notesExpanded;
    private bool _todosExpanded;
    private int _notesAnimToken;
    private int _todosAnimToken;
    private bool _busy;
    private bool _primaryEnabled;
    private bool _canExport = true;
    private bool _canImport = true;
    private PaperBodyTheme? _theme;
    private bool _sizeHooked;

    public DataIoMode Mode { get; private set; } = DataIoMode.Export;

    public event EventHandler? ModeChanged;
    public event EventHandler? NoteSelectionChanged;
    public event EventHandler? TodoSelectionChanged;
    public event EventHandler? PrimaryActionRequested;
    public event EventHandler? ChooseImportFileRequested;

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

        NotesList.ItemsSource = _noteRows;
        TodosList.ItemsSource = _todoRows;

        WireEvents();
        ApplyModeVisuals(DataIoMode.Export);
        ApplyExpansion();
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
            ? "点击条目展开并选择要导出的具体内容"
            : "点击条目展开并选择要导入的具体内容";
        FormatRow.Visibility = isExport ? Visibility.Visible : Visibility.Collapsed;
        StrategyRow.Visibility = isExport ? Visibility.Collapsed : Visibility.Visible;
        EstimateTitle.Text = isExport ? "导出预估" : "导入预估";
        PrimaryButton.Content = isExport ? "导出所选内容" : "导入所选内容";

        _notesExpanded = false;
        _todosExpanded = false;
        ApplyExpansion();
    }

    // ---------- 展开 / 收起 ----------

    private void OnNotesRowClick(object sender, MouseButtonEventArgs e)
    {
        _notesExpanded = !_notesExpanded;
        ApplyExpansion();
    }

    private void OnTodosRowClick(object sender, MouseButtonEventArgs e)
    {
        _todosExpanded = !_todosExpanded;
        ApplyExpansion();
    }

    private void ApplyExpansion()
    {
        AnimatePanel(
            NotesPanel,
            NotesContent,
            NotesChevronRotate,
            _notesExpanded,
            ++_notesAnimToken);
        AnimatePanel(
            TodosPanel,
            TodosContent,
            TodosChevronRotate,
            _todosExpanded,
            ++_todosAnimToken);
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
        NotesRow.IsEnabled = notesEnabled;
        NotesRow.Opacity = notesEnabled ? 1.0 : 0.45;
        TodosRow.IsEnabled = todosEnabled;
        TodosRow.Opacity = todosEnabled ? 1.0 : 0.45;

        if ((!notesEnabled && _notesExpanded) || (!todosEnabled && _todosExpanded))
        {
            _notesExpanded = _notesExpanded && notesEnabled;
            _todosExpanded = _todosExpanded && todosEnabled;
            ApplyExpansion();
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
        NotesSelectAll.IsEnabled = !busy;
        TodosSelectAll.IsEnabled = !busy;
        NotesInvertButton.IsEnabled = !busy;
        TodosInvertButton.IsEnabled = !busy;
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

    // ---------- 行与选择 ----------

    /// <summary>刷新两个类型的具体条目；selectedKeys 为 null 时默认全选可选项。</summary>
    public void SetRows(IReadOnlyList<PaperRowItem> rows, IReadOnlyCollection<string>? selectedKeys)
    {
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
                _todoRows.Add(row);
            }
            else
            {
                _noteRows.Add(row);
            }
        }

        _suppressSelectionEvents = false;
        UpdateSelectAllState("note");
        UpdateSelectAllState("todo");
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

        UpdateSelectAllState(row.Type);
        RaiseSelectionChanged(row.Type);
    }

    private void UpdateSelectAllState(string type)
    {
        var rows = type == "todo" ? _todoRows : _noteRows;
        var checkBox = type == "todo" ? TodosSelectAll : NotesSelectAll;
        var selectable = rows.Where(r => r.CanSelect).ToList();
        checkBox.IsChecked = selectable.Count > 0 && selectable.All(r => r.IsSelected);
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

    private void OnNotesSelectAllClick(object sender, RoutedEventArgs e) =>
        SetAllOfType("note", NotesSelectAll.IsChecked == true);

    private void OnTodosSelectAllClick(object sender, RoutedEventArgs e) =>
        SetAllOfType("todo", TodosSelectAll.IsChecked == true);

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
        UpdateSelectAllState(type);
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
        UpdateSelectAllState(type);
        RaiseSelectionChanged(type);
    }

    private void WireEvents()
    {
        ExportModeButton.Checked += OnModeChecked;
        ImportModeButton.Checked += OnModeChecked;

        NotesRow.MouseLeftButtonUp += OnNotesRowClick;
        TodosRow.MouseLeftButtonUp += OnTodosRowClick;
        NotesSelectAll.Click += OnNotesSelectAllClick;
        TodosSelectAll.Click += OnTodosSelectAllClick;
        NotesInvertButton.Click += OnNotesInvertClick;
        TodosInvertButton.Click += OnTodosInvertClick;

        PrimaryButton.Click += (_, _) => PrimaryActionRequested?.Invoke(this, EventArgs.Empty);
        ChooseFileButton.Click += (_, _) => ChooseImportFileRequested?.Invoke(this, EventArgs.Empty);
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
