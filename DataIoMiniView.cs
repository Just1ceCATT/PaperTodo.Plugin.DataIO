using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace PaperTodo.Plugin.DataIo;

/// <summary>
/// 贴边胶囊悬停时的迷你面板：只读展示最近的导入/导出记录。
/// 宿主要求 CreateMiniView 返回独立的纯 WPF 控件树，因此这里不引用正文视图的任何元素。
/// 面板不捕获键盘焦点，也不放交互控件；点击空白处仍由宿主打开完整纸片。
/// </summary>
internal sealed class DataIoMiniView : Grid
{
    private const int MaxVisibleEntries = 4;

    private static readonly FontFamily IconFontFamily =
        new("Segoe Fluent Icons, Segoe MDL2 Assets");

    private readonly TextBlock _headerTitle;
    private readonly TextBlock _headerCount;
    private readonly StackPanel _entries;
    private readonly Grid _emptyState;
    private readonly TextBlock _emptyGlyph;
    private readonly TextBlock _emptyTitle;
    private readonly TextBlock _emptyHint;
    private readonly TextBlock _footer;

    private SolidColorBrush _textBrush = Brushes.Black;
    private SolidColorBrush _weakBrush = Brushes.Gray;
    private SolidColorBrush _accentBrush = Brushes.DodgerBlue;
    private SolidColorBrush _accentSurfaceBrush = Brushes.Transparent;
    private SolidColorBrush _warningBrush = Brushes.OrangeRed;
    private SolidColorBrush _warningSurfaceBrush = Brushes.Transparent;
    private SolidColorBrush _neutralSurfaceBrush = Brushes.Transparent;
    private SolidColorBrush _dividerBrush = Brushes.Transparent;
    private FontFamily _fontFamily = new("Microsoft YaHei UI");

    public DataIoMiniView(PaperBodyTheme theme)
    {
        Background = Brushes.Transparent;
        SnapsToDevicePixels = true;
        AutomationProperties.SetName(this, "DataIO 导入导出记录");

        // 宿主会把迷你视图自身的 Margin 归零，内边距必须放在内部容器上。
        var content = new Grid { Margin = new Thickness(12, 9, 12, 9) };
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition());
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Children.Add(content);

        var header = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _headerTitle = new TextBlock
        {
            Text = "导入导出记录",
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        header.Children.Add(_headerTitle);

        _headerCount = new TextBlock
        {
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(8, 0, 0, 0)
        };
        Grid.SetColumn(_headerCount, 1);
        header.Children.Add(_headerCount);
        content.Children.Add(header);

        var body = new Grid { ClipToBounds = true };
        _entries = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
        body.Children.Add(_entries);

        (_emptyState, var emptyPanel) = BuildEmptyState();
        _emptyGlyph = (TextBlock)emptyPanel.Children[0];
        _emptyTitle = (TextBlock)emptyPanel.Children[1];
        _emptyHint = (TextBlock)emptyPanel.Children[2];
        _emptyState.Visibility = Visibility.Collapsed;
        body.Children.Add(_emptyState);
        Grid.SetRow(body, 1);
        content.Children.Add(body);

        _footer = new TextBlock
        {
            Text = "点击卡片打开 DataIO",
            FontSize = 10,
            Margin = new Thickness(0, 4, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetRow(_footer, 2);
        content.Children.Add(_footer);

        ApplyTheme(theme);
    }

    /// <summary>
    /// 用最新历史与主题重建内容。宿主会缓存控件树，因此会话在记录变化、
    /// 主题变化和面板重新可见时都会调用这里，而不是每次重新创建控件。
    /// </summary>
    public void Update(IReadOnlyList<DataIoHistoryEntry>? entries, PaperBodyTheme theme)
    {
        ApplyTheme(theme);

        var visible = (entries ?? [])
            .Where(entry => entry is not null)
            .Take(MaxVisibleEntries)
            .ToList();

        _entries.Children.Clear();
        for (var index = 0; index < visible.Count; index++)
        {
            _entries.Children.Add(BuildEntry(visible[index], index));
        }

        var hasEntries = visible.Count > 0;
        _entries.Visibility = hasEntries ? Visibility.Visible : Visibility.Collapsed;
        _emptyState.Visibility = hasEntries ? Visibility.Collapsed : Visibility.Visible;
        _headerCount.Text = hasEntries ? $"最近 {visible.Count} 条" : "";
    }

    private void ApplyTheme(PaperBodyTheme theme)
    {
        _textBrush = ThemeBrushes.MakeBrush(theme.TextColor, Color.FromRgb(0x20, 0x20, 0x20));
        _weakBrush = ThemeBrushes.MakeBrush(theme.WeakTextColor, Color.FromRgb(0x70, 0x70, 0x70));
        _accentBrush = ThemeBrushes.MakeBrush(theme.AccentColor, Color.FromRgb(0x2F, 0x6B, 0xFF));
        _accentSurfaceBrush = ThemeBrushes.MakeBrushWithAlpha(
            theme.AccentColor,
            theme.IsDark ? (byte)46 : (byte)26,
            Color.FromRgb(0x2F, 0x6B, 0xFF));
        _warningBrush = ThemeBrushes.MakeBrush(
            theme.IsDark ? "#FFD97B72" : "#FFC44B42",
            Color.FromRgb(0xC4, 0x4B, 0x42));
        _warningSurfaceBrush = ThemeBrushes.MakeBrushWithAlpha(
            theme.IsDark ? "#FFD97B72" : "#FFC44B42",
            theme.IsDark ? (byte)40 : (byte)24,
            Color.FromRgb(0xC4, 0x4B, 0x42));
        _neutralSurfaceBrush = ThemeBrushes.MakeBrushWithAlpha(
            theme.WeakTextColor,
            theme.IsDark ? (byte)34 : (byte)20,
            Color.FromRgb(0x70, 0x70, 0x70));
        _dividerBrush = ThemeBrushes.MakeBrush(
            theme.IsDark ? "#1FFFFFFF" : "#14000000",
            Color.FromArgb(0x14, 0x00, 0x00, 0x00));
        _fontFamily = new FontFamily(string.IsNullOrWhiteSpace(theme.FontFamily)
            ? "Microsoft YaHei UI"
            : theme.FontFamily);

        _headerTitle.FontFamily = _fontFamily;
        _headerTitle.Foreground = _weakBrush;
        _headerCount.FontFamily = _fontFamily;
        _headerCount.Foreground = _weakBrush;

        _footer.FontFamily = _fontFamily;
        _footer.Foreground = _weakBrush;

        _emptyGlyph.Foreground = _weakBrush;
        _emptyTitle.FontFamily = _fontFamily;
        _emptyTitle.Foreground = _textBrush;
        _emptyHint.FontFamily = _fontFamily;
        _emptyHint.Foreground = _weakBrush;
    }

    private Border BuildEntry(DataIoHistoryEntry entry, int index)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new Border
        {
            Width = 26,
            Height = 26,
            CornerRadius = new CornerRadius(8),
            Background = _accentSurfaceBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = entry.IsExport ? DataIoGlyphs.Export : DataIoGlyphs.Import,
                FontFamily = IconFontFamily,
                FontSize = 12,
                Foreground = _accentBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        grid.Children.Add(icon);

        var textPanel = new StackPanel
        {
            Margin = new Thickness(8, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center
        };

        // 标题行：短标题 + 数量徽标，避免把数量写成一长串。
        var titleRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        titleRow.Children.Add(new TextBlock
        {
            Text = DataIoHistoryText.MiniTitle(entry),
            FontFamily = _fontFamily,
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = _textBrush,
            VerticalAlignment = VerticalAlignment.Center
        });
        foreach (var item in DataIoHistoryText.Badges(entry))
        {
            titleRow.Children.Add(BuildBadge(item));
        }

        textPanel.Children.Add(titleRow);

        // 副标题：格式 + 文件名；长名中部省略，扩展名始终保留。
        var subtitle = new TextBlock
        {
            Text = DataIoHistoryText.MiniSubtitle(entry),
            FontFamily = _fontFamily,
            FontSize = 10.5,
            Foreground = _weakBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            Margin = new Thickness(0, 2, 0, 0)
        };
        if (!string.IsNullOrWhiteSpace(entry.FilePath))
        {
            subtitle.ToolTip = entry.FilePath;
        }

        textPanel.Children.Add(subtitle);
        Grid.SetColumn(textPanel, 1);
        grid.Children.Add(textPanel);

        var time = new TextBlock
        {
            Text = DataIoHistoryText.FormatTime(entry.At, DateTimeOffset.Now),
            FontFamily = _fontFamily,
            FontSize = 10,
            Foreground = _weakBrush,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(8, 1, 0, 0)
        };
        Grid.SetColumn(time, 2);
        grid.Children.Add(time);

        return new Border
        {
            BorderBrush = _dividerBrush,
            BorderThickness = new Thickness(0, index == 0 ? 0 : 1, 0, 0),
            Padding = new Thickness(0, index == 0 ? 0 : 6, 0, 6),
            Child = grid
        };
    }

    private Border BuildBadge(DataIoHistoryBadge badge)
    {
        var (background, foreground) = badge.Tone switch
        {
            DataIoHistoryBadgeTone.Accent => (_accentSurfaceBrush, _accentBrush),
            DataIoHistoryBadgeTone.Warning => (_warningSurfaceBrush, _warningBrush),
            _ => (_neutralSurfaceBrush, _textBrush)
        };

        return new Border
        {
            Background = background,
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(5, 1, 5, 1),
            Margin = new Thickness(5, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = badge.Text,
                FontFamily = _fontFamily,
                FontSize = 9.5,
                Foreground = foreground
            }
        };
    }

    private static (Grid Root, StackPanel Panel) BuildEmptyState()
    {
        var panel = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(12, 0, 12, 0)
        };

        panel.Children.Add(new TextBlock
        {
            Text = DataIoGlyphs.History,
            FontFamily = IconFontFamily,
            FontSize = 20,
            HorizontalAlignment = HorizontalAlignment.Center,
            Opacity = 0.75
        });
        panel.Children.Add(new TextBlock
        {
            Text = "还没有导入导出记录",
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 5, 0, 0)
        });
        panel.Children.Add(new TextBlock
        {
            Text = "在 DataIO 中导入或导出后，最近记录会显示在这里",
            FontSize = 10.5,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 3, 0, 0)
        });

        var root = new Grid();
        root.Children.Add(panel);
        return (root, panel);
    }
}
