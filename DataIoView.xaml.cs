using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PaperTodo.Plugin;

namespace PaperTodo.Plugin.DataIo;

/// <summary>
/// 工具纸片的主体界面。只负责展示与收集用户选择，不直接调用宿主写 API。
/// 所有颜色通过本地资源注入，避免依赖宿主资源字典。
/// </summary>
public partial class DataIoView : UserControl
{
    private readonly ObservableCollection<PaperRowItem> _rows = [];
    private readonly List<PaperRowItem> _allRows = [];
    private bool _suppressFilterEvents;
    private bool _selectionInitialized;
    private bool _busy;
    private bool _canExport = true;
    private bool _canImport = true;

    public event EventHandler? ExportRequested;
    public event EventHandler? ImportRequested;
    public event EventHandler? FilterChanged;

    public DataIoView()
    {
        InitializeComponent();
        PaperList.ItemsSource = _rows;
        BuildFilterItems();

        SelectAllCheckBox.Click += (_, _) => SetAllSelected(SelectAllCheckBox.IsChecked == true);
        InvertButton.Click += (_, _) => InvertSelection();
        FilterBox.SelectionChanged += OnFilterOrSearchChanged;
        SearchBox.TextChanged += OnFilterOrSearchChanged;
        ExportButton.Click += (_, _) => ExportRequested?.Invoke(this, EventArgs.Empty);
        ImportButton.Click += (_, _) => ImportRequested?.Invoke(this, EventArgs.Empty);

        ApplyFilter();
    }

    public string CurrentFilter =>
        (FilterBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "all";

    public string CurrentSearch => SearchBox.Text?.Trim() ?? "";

    /// <summary>恢复上次使用的筛选项与搜索词。</summary>
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

    /// <summary>刷新列表；首次加载时默认勾选所有可导出纸片，之后保留用户勾选。</summary>
    public void SetRows(IReadOnlyList<PaperRowItem> rows)
    {
        var selected = new HashSet<string>(
            _allRows.Where(r => r.IsSelected).Select(r => r.PaperId),
            StringComparer.Ordinal);

        _allRows.Clear();
        foreach (var row in rows)
        {
            row.IsSelected = _selectionInitialized ? selected.Contains(row.PaperId) : row.CanSelect;
            _allRows.Add(row);
        }

        _selectionInitialized = true;
        ApplyFilter();
    }

    public IReadOnlyList<string> GetSelectedPaperIds() =>
        _allRows
            .Where(r => r.IsSelected && r.CanSelect)
            .Select(r => r.PaperId)
            .ToList();

    public void SetStatus(string text) => StatusText.Text = text;

    public void SetBusy(bool busy)
    {
        _busy = busy;
        BusyBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ExportButton.IsEnabled = !busy && _canExport;
        ImportButton.IsEnabled = !busy && _canImport;
        SelectAllCheckBox.IsEnabled = !busy;
        InvertButton.IsEnabled = !busy;
    }

    public void SetAvailability(bool canExport, bool canImport)
    {
        _canExport = canExport;
        _canImport = canImport;
        ExportButton.IsEnabled = !_busy && canExport;
        ImportButton.IsEnabled = !_busy && canImport;
    }

    /// <summary>按宿主主题生成画刷与字号资源。</summary>
    public void ApplyTheme(PaperBodyTheme theme, IPaperBodyControls? controls = null)
    {
        var scale = theme.FontScale <= 0 ? 1.0 : Math.Clamp(theme.FontScale, 0.7, 2.5);

        Resources["DataIoTextBrush"] = MakeBrush(theme.TextColor, Color.FromRgb(0x20, 0x20, 0x20));
        Resources["DataIoWeakBrush"] = MakeBrush(theme.WeakTextColor, Color.FromRgb(0x70, 0x70, 0x70));
        Resources["DataIoBorderBrush"] = MakeBrush(theme.BorderColor, Color.FromRgb(0x80, 0x70, 0x50));
        Resources["DataIoAccentBrush"] = MakeBrush(theme.AccentColor, Color.FromRgb(0xB0, 0x7A, 0x31));
        Resources["DataIoSurfaceBrush"] = MakeBrush(
            theme.IsDark ? "#18FFFFFF" : "#0C000000",
            Color.FromArgb(0x0C, 0x00, 0x00, 0x00));
        Resources["DataIoAccentSurfaceBrush"] = MakeBrushWithAlpha(
            theme.AccentColor,
            theme.IsDark ? (byte)46 : (byte)26,
            Color.FromRgb(0xB0, 0x7A, 0x31));
        Resources["DataIoWarningBrush"] = MakeBrush(
            theme.IsDark ? "#FFD97B72" : "#FFC44B42",
            Color.FromRgb(0xC4, 0x4B, 0x42));

        var fontFamily = new FontFamily(string.IsNullOrWhiteSpace(theme.FontFamily)
            ? "Microsoft YaHei UI"
            : theme.FontFamily);
        Resources["DataIoFontFamily"] = fontFamily;
        Resources["DataIoFontSize"] = 12.5 * scale;
        Resources["DataIoTitleFontSize"] = 14.5 * scale;
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

    private void OnFilterOrSearchChanged(object? sender, EventArgs e)
    {
        if (_suppressFilterEvents)
        {
            return;
        }

        ApplyFilter();
        FilterChanged?.Invoke(this, EventArgs.Empty);
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

        var selectable = _rows.Where(r => r.CanSelect).ToList();
        SelectAllCheckBox.IsChecked = selectable.Count > 0 && selectable.All(r => r.IsSelected);
    }

    private void SetAllSelected(bool value)
    {
        foreach (var row in _rows.Where(r => r.CanSelect))
        {
            row.IsSelected = value;
        }

        SelectAllCheckBox.IsChecked = value;
    }

    private void InvertSelection()
    {
        foreach (var row in _rows.Where(r => r.CanSelect))
        {
            row.IsSelected = !row.IsSelected;
        }

        var selectable = _rows.Where(r => r.CanSelect).ToList();
        SelectAllCheckBox.IsChecked = selectable.Count > 0 && selectable.All(r => r.IsSelected);
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
