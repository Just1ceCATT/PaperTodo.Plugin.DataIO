using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using PaperTodo.Plugin;

namespace PaperTodo.Plugin.DataIo;

/// <summary>
/// 与宿主 PaperNoticeDialog 同款的原生风格模态对话框：
/// 圆角卡片、纸面色、Tint 半透明按钮，无图标与标题栏关闭按钮。
/// </summary>
public partial class DataIoDialog : Window
{
    private readonly double _scale;
    private readonly Color _tint;
    private readonly Dictionary<ImportPaper, ImportUpdateAction> _updateChoices = [];

    public DataIoDialog(
        PaperBodyTheme theme,
        Color tint,
        string title,
        string primaryText,
        string? secondaryText = null)
    {
        InitializeComponent();

        _scale = theme.FontScale <= 0 ? 1.0 : Math.Clamp(theme.FontScale, 0.8, 2.4);
        _tint = tint;
        ApplyTheme(theme);

        TitleText.Text = title;
        TitleText.FontSize = 16.0 * _scale;

        BuildButtons(primaryText, secondaryText);
        PreviewKeyDown += OnPreviewKeyDown;

        // 无标题栏窗口：标题区域和空白区域可用于拖动。
        TitleText.MouseLeftButtonDown += OnDragAreaMouseDown;
        Shell.MouseLeftButtonDown += OnShellMouseDown;
    }

    /// <summary>消息型内容：自动换行的多行文本。</summary>
    public void SetMessage(string message)
    {
        BodyPanel.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = Width - 38,
            LineHeight = 20.0 * _scale,
            Margin = new Thickness(0, 2, 0, 0),
            FontSize = 13.0 * _scale,
            Foreground = (Brush)Resources["DialogWeakBrush"]
        });
    }

    /// <summary>结果型内容：统计行 + 可选详情（逐条渲染，条目之间留出间距）。</summary>
    public void SetStats(
        IReadOnlyList<(string Label, string Value)> stats,
        IReadOnlyList<string>? details)
    {
        var rows = new StackPanel();
        foreach (var (label, value) in stats)
        {
            var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            row.Children.Add(new TextBlock
            {
                Text = label,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12.5 * _scale,
                Foreground = (Brush)Resources["DialogWeakBrush"]
            });

            var valueText = new TextBlock
            {
                Text = value,
                Margin = new Thickness(16, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 13.0 * _scale,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)Resources["DialogTextBrush"]
            };
            Grid.SetColumn(valueText, 1);
            row.Children.Add(valueText);

            rows.Children.Add(row);
        }

        BodyPanel.Children.Add(new Border
        {
            Background = TintBrush(14),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 6, 10, 6),
            Child = rows
        });

        if (details is { Count: > 0 })
        {
            var detailPanel = new StackPanel
            {
                Margin = new Thickness(0, 8, 0, 0)
            };
            foreach (var detail in details)
            {
                detailPanel.Children.Add(new TextBlock
                {
                    Text = detail,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = Width - 38,
                    LineHeight = 18.0 * _scale,
                    FontSize = 12.0 * _scale,
                    Foreground = (Brush)Resources["DialogWeakBrush"],
                    // 条目之间留出间距，长文本换行仍沿用行高。
                    Margin = new Thickness(0, 0, 0, 6)
                });
            }

            BodyPanel.Children.Add(detailPanel);
        }
    }

    internal IReadOnlyDictionary<ImportPaper, ImportUpdateAction> UpdateChoices => _updateChoices;

    /// <summary>
    /// 更新预览：逐张展示本地内容与导入内容的差异，并让用户选择跳过/更新/新增。
    /// 默认选择“跳过”，把覆盖本地数据变成必须显式确认的破坏性动作。
    /// </summary>
    internal void SetUpdatePreview(
        IReadOnlyList<ImportPaper> papers,
        IReadOnlyList<string>? unrecoverable,
        bool canCreate)
    {
        // 固定宽度，差异文本在剩余空间内自动换行。
        Width = 660;
        MinWidth = 660;
        MaxWidth = 660;

        BodyPanel.Children.Add(new TextBlock
        {
            Text = "以下纸片匹配到本地已有内容。默认全部“跳过”，只有显式选择“更新”的纸片才会被覆盖：",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12.5 * _scale,
            Foreground = (Brush)Resources["DialogWeakBrush"],
            Margin = new Thickness(0, 0, 0, 8)
        });

        if (unrecoverable is { Count: > 0 })
        {
            BodyPanel.Children.Add(BuildUnrecoverablePanel(unrecoverable));
        }

        for (var index = 0; index < papers.Count; index++)
        {
            BodyPanel.Children.Add(BuildUpdateCard(papers[index], index, canCreate));
        }
    }

    /// <summary>文件层面无法完整还原的内容汇总，与更新卡片一起展示，避免用户只看逐条差异。</summary>
    private FrameworkElement BuildUnrecoverablePanel(IReadOnlyList<string> lines)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = "以下内容无法完整还原：",
            FontSize = 12.0 * _scale,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)Resources["DialogWarningBrush"],
            Margin = new Thickness(0, 0, 0, 3)
        });

        foreach (var line in lines)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "· " + line,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11.5 * _scale,
                Foreground = (Brush)Resources["DialogWeakBrush"],
                Margin = new Thickness(0, 0, 0, 2)
            });
        }

        return new Border
        {
            Background = TintBrush(16),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 7, 10, 7),
            Margin = new Thickness(0, 0, 0, 10),
            Child = panel
        };
    }

    private FrameworkElement BuildUpdateCard(ImportPaper paper, int index, bool canCreate)
    {
        var card = new StackPanel
        {
            Margin = new Thickness(0, 0, 0, 10)
        };

        card.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(paper.Source.Title) ? "（无标题）" : paper.Source.Title,
            FontSize = 13.0 * _scale,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Resources["DialogTextBrush"]
        });

        var typeLabel = string.Equals(paper.Source.Type, "note", StringComparison.OrdinalIgnoreCase)
            ? "笔记"
            : "待办";
        var targetLabel = string.IsNullOrWhiteSpace(paper.UpdateTargetTitle)
            ? "本地已有纸片"
            : $"本地纸片：{paper.UpdateTargetTitle}";
        card.Children.Add(new TextBlock
        {
            Text = $"{typeLabel} · {targetLabel}",
            FontSize = 11.5 * _scale,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Resources["DialogWeakBrush"],
            Margin = new Thickness(0, 2, 0, 0)
        });

        if (paper.TargetLocallyModified)
        {
            card.Children.Add(new TextBlock
            {
                Text = "该纸片在导入后被修改过；更新会覆盖本地修改。",
                FontSize = 11.5 * _scale,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)Resources["DialogWarningBrush"],
                Margin = new Thickness(0, 3, 0, 0)
            });
        }

        if (paper.TargetImagesAtRisk)
        {
            card.Children.Add(new TextBlock
            {
                Text = "文件中的图片引用已被替换为“图片缺失”标记，更新会破坏本地图片引用；只能选择“跳过”或“新增”。",
                FontSize = 11.5 * _scale,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)Resources["DialogWarningBrush"],
                Margin = new Thickness(0, 3, 0, 0)
            });
        }

        // 笔记正文不可用时更新没有意义（会抛 content_unavailable），提前禁用该选项。
        var contentUnavailable = string.Equals(
                paper.Source.Type,
                "note",
                StringComparison.OrdinalIgnoreCase) &&
            paper.Source.ContentAvailable != true;
        if (contentUnavailable)
        {
            card.Children.Add(new TextBlock
            {
                Text = "文件未包含可用的笔记正文，只能选择“跳过”或“新增”。",
                FontSize = 11.5 * _scale,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)Resources["DialogWarningBrush"],
                Margin = new Thickness(0, 3, 0, 0)
            });
        }

        var updateAllowed = !paper.TargetImagesAtRisk && !contentUnavailable;
        var choices = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 6, 0, 5)
        };
        AddChoice(choices, paper, index, ImportUpdateAction.Skip, "跳过");
        AddChoice(
            choices,
            paper,
            index,
            ImportUpdateAction.Update,
            "更新",
            updateAllowed,
            updateAllowed ? null : "当前内容不适合更新，请选择跳过或新增");
        AddChoice(
            choices,
            paper,
            index,
            ImportUpdateAction.CreateNew,
            "新增",
            canCreate,
            canCreate ? null : "缺少 papers.create 权限，无法新增纸片");
        card.Children.Add(choices);

        card.Children.Add(BuildDiffPanel(paper.UpdateDiff));
        return card;
    }

    private void AddChoice(
        Panel host,
        ImportPaper paper,
        int index,
        ImportUpdateAction action,
        string label,
        bool enabled = true,
        string? disabledToolTip = null)
    {
        var radio = new RadioButton
        {
            Content = label,
            GroupName = $"DataIoUpdateChoice{index}",
            Style = (Style)FindResource("UpdateChoice"),
            FontSize = 12.0 * _scale,
            IsEnabled = enabled,
            ToolTip = disabledToolTip,
            // 默认“跳过”：覆盖本地数据必须是显式选择。
            IsChecked = action == ImportUpdateAction.Skip
        };
        _updateChoices[paper] = ImportUpdateAction.Skip;
        ToolTipService.SetShowOnDisabled(radio, true);
        radio.Checked += (_, _) => _updateChoices[paper] = action;
        host.Children.Add(radio);
    }

    private FrameworkElement BuildDiffPanel(IReadOnlyList<DiffLine>? diff)
    {
        var panel = new StackPanel();
        var mono = new FontFamily("Consolas, Microsoft YaHei UI");

        if (diff is not { Count: > 0 } ||
            diff.All(line => line.Kind == ' '))
        {
            panel.Children.Add(new TextBlock
            {
                Text = "（内容无变化）",
                FontSize = 11.5 * _scale,
                Foreground = (Brush)Resources["DialogWeakBrush"]
            });
        }
        else
        {
            panel.Children.Add(BuildDiffHeader(mono));

            foreach (var line in diff)
            {
                var row = new Grid
                {
                    Margin = new Thickness(0, 0, 0, 1),
                    Background = (Brush)Resources[line.Kind switch
                    {
                        '+' => "DialogAddedBackgroundBrush",
                        '-' => "DialogRemovedBackgroundBrush",
                        _ => "DialogTransparentBrush"
                    }]
                };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
                row.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = new GridLength(1, GridUnitType.Star)
                });

                var isGap = line.Kind == '…';
                var foreground = (Brush)Resources[line.Kind switch
                {
                    '+' => "DialogAddedBrush",
                    '-' => "DialogRemovedBrush",
                    '…' => "DialogGapBrush",
                    _ => "DialogWeakBrush"
                }];
                AddCell(row, 0, line.OldLine, mono, foreground);
                AddCell(row, 1, line.NewLine, mono, foreground);
                AddCell(row, 2, line.Kind == ' ' ? "" : line.Kind.ToString(), mono, foreground);

                var content = new TextBlock
                {
                    Text = string.IsNullOrEmpty(line.Text) ? " " : line.Text,
                    FontFamily = mono,
                    FontSize = 11.5 * _scale,
                    FontStyle = isGap ? FontStyles.Italic : FontStyles.Normal,
                    Foreground = foreground,
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Top
                };
                Grid.SetColumn(content, 3);
                row.Children.Add(content);
                panel.Children.Add(row);
            }
        }

        return new Border
        {
            Background = TintBrush(8),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(9, 7, 9, 7),
            Child = panel
        };
    }

    private FrameworkElement BuildDiffHeader(FontFamily font)
    {
        var header = new Grid { Margin = new Thickness(0, 0, 0, 3) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        header.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });

        var weak = (Brush)Resources["DialogWeakBrush"];
        AddCell(header, 0, "旧", font, weak);
        AddCell(header, 1, "新", font, weak);
        var label = new TextBlock
        {
            Text = "差异内容",
            FontSize = 10.5 * _scale,
            Foreground = weak,
            Opacity = 0.72,
            VerticalAlignment = VerticalAlignment.Top
        };
        Grid.SetColumn(label, 3);
        header.Children.Add(label);
        return header;
    }

    private void AddCell(Grid row, int column, int lineNumber, FontFamily font, Brush foreground) =>
        AddCell(
            row,
            column,
            lineNumber == 0 ? "" : lineNumber.ToString(CultureInfo.InvariantCulture),
            font,
            foreground);

    private void AddCell(Grid row, int column, string value, FontFamily font, Brush foreground)
    {
        var text = new TextBlock
        {
            Text = value,
            FontFamily = font,
            FontSize = 10.5 * _scale,
            Foreground = foreground,
            Opacity = 0.72,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 0, 6, 0)
        };
        Grid.SetColumn(text, column);
        row.Children.Add(text);
    }

    private void OnDragAreaMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // 窗口尚未完成显示时忽略拖动。
        }

        e.Handled = true;
    }

    private void OnShellMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed ||
            e.OriginalSource is DependencyObject source && IsInteractive(source))
        {
            return;
        }

        OnDragAreaMouseDown(sender, e);
    }

    private bool IsInteractive(DependencyObject source)
    {
        for (var node = source;
             node != null && !ReferenceEquals(node, Shell);
             node = VisualTreeHelper.GetParent(node))
        {
            if (node is ButtonBase or TextBox or ScrollBar or RadioButton)
            {
                return true;
            }
        }

        return false;
    }

    public bool? ShowModal(Window? owner)
    {
        if (owner is { IsVisible: true })
        {
            Owner = owner;
            Topmost = owner.Topmost;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        return ShowDialog();
    }

    private void ApplyTheme(PaperBodyTheme theme)
    {
        Resources["DialogTextBrush"] = MakeBrush(theme.TextColor, Color.FromRgb(0xE8, 0xE8, 0xE8));
        Resources["DialogWeakBrush"] = MakeBrush(theme.WeakTextColor, Color.FromRgb(0x9A, 0x9A, 0x9A));
        Resources["DialogPaperBrush"] = MakeBrush(
            theme.PaperColor,
            theme.IsDark ? Color.FromRgb(0x21, 0x1F, 0x1C) : Color.FromRgb(0xFF, 0xF9, 0xEA));
        Resources["DialogBorderBrush"] = MakeBrush(theme.BorderColor, Color.FromRgb(0x4C, 0x45, 0x3D));
        Resources["DialogButtonHoverBrush"] = TintBrush(46);
        Resources["DialogTintBrush"] = TintBrush(32);
        Resources["DialogWarningBrush"] = MakeBrush(
            theme.IsDark ? "#E0A458" : "#9A5B00",
            Color.FromRgb(0xE0, 0xA4, 0x58));
        var addedColor = theme.IsDark
            ? Color.FromRgb(0x7F, 0xD9, 0x9A)
            : Color.FromRgb(0x1E, 0x7F, 0x45);
        var removedColor = theme.IsDark
            ? Color.FromRgb(0xF0, 0x8A, 0x80)
            : Color.FromRgb(0xB3, 0x26, 0x1E);
        Resources["DialogAddedBrush"] = FreezeBrush(addedColor);
        Resources["DialogRemovedBrush"] = FreezeBrush(removedColor);
        Resources["DialogAddedBackgroundBrush"] = AlphaBrush(22, addedColor);
        Resources["DialogRemovedBackgroundBrush"] = AlphaBrush(22, removedColor);
        Resources["DialogTransparentBrush"] = Brushes.Transparent;
        // 省略行提示使用中性灰，避免被误认为警告。
        Resources["DialogGapBrush"] = MakeBrush(
            theme.IsDark ? "#8C8C8C" : "#6B6B6B",
            Color.FromRgb(0x8C, 0x8C, 0x8C));

        FontFamily = new FontFamily(string.IsNullOrWhiteSpace(theme.FontFamily)
            ? "Microsoft YaHei UI"
            : theme.FontFamily);
        FontSize = 12.0 * _scale;
    }

    private void BuildButtons(string primaryText, string? secondaryText)
    {
        var hasSecondary = !string.IsNullOrWhiteSpace(secondaryText);

        if (hasSecondary)
        {
            var secondary = CreateButton(secondaryText!, TintBrush(28));
            secondary.Click += (_, _) => CloseWith(false);
            ButtonPanel.Children.Add(secondary);
        }

        var primary = CreateButton(primaryText, TintBrush(hasSecondary ? 44 : 28));
        primary.IsDefault = true;
        if (hasSecondary)
        {
            primary.Margin = new Thickness(8, 0, 0, 0);
        }

        primary.Click += (_, _) => CloseWith(true);
        ButtonPanel.Children.Add(primary);
    }

    private Button CreateButton(string text, Brush background)
    {
        return new Button
        {
            Content = text,
            Style = (Style)FindResource("DialogButton"),
            FontSize = 13.0 * _scale,
            Foreground = (Brush)Resources["DialogTextBrush"],
            Background = background
        };
    }

    private SolidColorBrush TintBrush(int alpha)
    {
        var brush = new SolidColorBrush(Color.FromArgb((byte)alpha, _tint.R, _tint.G, _tint.B));
        brush.Freeze();
        return brush;
    }

    private void CloseWith(bool result)
    {
        DialogResult = result;
        Close();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CloseWith(false);
            e.Handled = true;
        }
    }

    private static SolidColorBrush FreezeBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static SolidColorBrush AlphaBrush(byte alpha, Color color) =>
        FreezeBrush(Color.FromArgb(alpha, color.R, color.G, color.B));

    private static SolidColorBrush MakeBrush(string? hex, Color fallback)
    {
        var color = fallback;
        if (!string.IsNullOrWhiteSpace(hex))
        {
            try
            {
                if (ColorConverter.ConvertFromString(hex) is Color parsed)
                {
                    color = parsed;
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

        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
