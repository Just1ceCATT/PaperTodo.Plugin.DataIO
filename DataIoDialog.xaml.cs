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

    /// <summary>每张更新卡片的三个选项按钮，供“全部更新/全部跳过”批量切换。</summary>
    private readonly Dictionary<ImportPaper, Dictionary<ImportUpdateAction, RadioButton>> _choiceButtons = [];

    /// <summary>更新预览的实时决策摘要与主按钮。</summary>
    private TextBlock? _updateSummaryText;
    private Button? _primaryButton;
    private int _newPaperCount;
    private IReadOnlyList<string> _unchangedTitles = [];

    public DataIoDialog(
        PaperBodyTheme theme,
        Color tint,
        string title,
        string primaryText,
        string? secondaryText = null)
    {
        InitializeComponent();

        _scale = ThemeBrushes.NormalizeScale(theme.FontScale, 0.8, 2.4);
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

    /// <summary>消息型内容：自动换行的多行文本，可选可折叠的预检提示。</summary>
    public void SetMessage(string message, IReadOnlyList<string>? warnings = null)
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

        if (warnings is { Count: > 0 })
        {
            BodyPanel.Children.Add(BuildWarningsPanel(warnings));
        }
    }

    /// <summary>
    /// 可折叠的预检提示列表。默认收起，点击标题展开；只有这一区域参与折叠，
    /// 确认正文与按钮保持常驻。
    /// </summary>
    private FrameworkElement BuildWarningsPanel(IReadOnlyList<string> warnings)
    {
        var content = new StackPanel
        {
            Margin = new Thickness(4, 2, 0, 2)
        };
        foreach (var warning in warnings)
        {
            content.Children.Add(new TextBlock
            {
                Text = "· " + warning,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = Width - 58,
                LineHeight = 18.0 * _scale,
                FontSize = 11.5 * _scale,
                Foreground = (Brush)Resources["DialogWeakBrush"],
                Margin = new Thickness(0, 0, 0, 3)
            });
        }

        var contentHost = new Border
        {
            Child = content,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 2, 0, 0)
        };

        var chevron = new TextBlock
        {
            Text = ((char)0xE76C).ToString(),
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 10.5 * _scale,
            Margin = new Thickness(0, 0, 7, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Resources["DialogWeakBrush"],
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new RotateTransform(0)
        };

        var header = new Button
        {
            Style = (Style)FindResource("WarningsToggle"),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    chevron,
                    new TextBlock
                    {
                        Text = $"预检提示（{warnings.Count}）",
                        FontSize = 12.0 * _scale,
                        VerticalAlignment = VerticalAlignment.Center,
                        Foreground = (Brush)Resources["DialogWeakBrush"]
                    }
                }
            }
        };

        header.Click += (_, _) =>
        {
            var expand = contentHost.Visibility != Visibility.Visible;
            contentHost.Visibility = expand ? Visibility.Visible : Visibility.Collapsed;
            if (chevron.RenderTransform is RotateTransform rotate)
            {
                rotate.Angle = expand ? 90 : 0;
            }
        };

        var panel = new StackPanel
        {
            Margin = new Thickness(0, 10, 0, 0)
        };
        panel.Children.Add(header);
        panel.Children.Add(contentHost);
        return panel;
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

    private readonly List<(string Name, CheckBox Box)> _workspaceChoices = [];
    private Style? _workspaceCheckBoxStyle;

    internal IReadOnlyList<string> SelectedWorkspaceNames =>
        _workspaceChoices
            .Where(choice => choice.Box.IsChecked == true)
            .Select(choice => choice.Name)
            .ToArray();

    /// <summary>备份工作区多选：默认勾选当前工作区；找不到当前工作区时勾选全部可读项。</summary>
    internal void SetCoreBackupWorkspaces(
        string fileName,
        IReadOnlyList<CoreBackupWorkspace> workspaces,
        string? currentWorkspaceName)
    {
        Width = 560;
        MinWidth = 560;
        MaxWidth = 560;

        var current = workspaces.FirstOrDefault(workspace =>
            workspace.IsReadable &&
            string.Equals(workspace.Name, currentWorkspaceName, StringComparison.OrdinalIgnoreCase));
        var selectAll = current is null;

        BodyPanel.Children.Add(new TextBlock
        {
            Text = $"{fileName} 包含 {workspaces.Count} 个工作区。选择一个或多个工作区进行导入；" +
                   "选择多个时会合并到当前工作区。",
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = Width - 38,
            LineHeight = 20.0 * _scale,
            FontSize = 12.5 * _scale,
            Foreground = (Brush)Resources["DialogWeakBrush"],
            Margin = new Thickness(0, 2, 0, 10)
        });

        var checkBoxStyle = WorkspaceCheckBoxStyle();
        foreach (var workspace in workspaces)
        {
            var isCurrent = ReferenceEquals(workspace, current);
            var shouldCheck = workspace.IsReadable && (selectAll || isCurrent);
            var check = new CheckBox
            {
                Style = checkBoxStyle,
                IsEnabled = workspace.IsReadable,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Center,
                Content = BuildWorkspaceChoiceContent(workspace, isCurrent)
            };
            check.Checked += (_, _) => RefreshWorkspaceSelection();
            check.Unchecked += (_, _) => RefreshWorkspaceSelection();

            BodyPanel.Children.Add(new Border
            {
                Background = TintBrush(14),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 0, 0, 6),
                Child = check
            });
            _workspaceChoices.Add((workspace.Name, check));

            // 等控件进入视觉树后再设置勾选，避免初始值被模板初始化覆盖。
            check.IsChecked = shouldCheck;
        }

        BodyPanel.Children.Add(new TextBlock
        {
            Text = "选择多个工作区时，所有纸片都会导入当前工作区，工作区边界不会保留。",
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = Width - 38,
            FontSize = 11.5 * _scale,
            Foreground = (Brush)Resources["DialogWeakBrush"],
            Margin = new Thickness(2, 4, 0, 0)
        });

        RefreshWorkspaceSelection();
    }

    private FrameworkElement BuildWorkspaceChoiceContent(
        CoreBackupWorkspace workspace,
        bool isCurrent)
    {
        var title = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        title.Children.Add(new TextBlock
        {
            Text = workspace.Name,
            FontSize = 13.0 * _scale,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)Resources["DialogTextBrush"],
            VerticalAlignment = VerticalAlignment.Center
        });
        if (isCurrent)
        {
            title.Children.Add(new Border
            {
                Margin = new Thickness(8, 0, 0, 0),
                Padding = new Thickness(6, 1, 6, 1),
                CornerRadius = new CornerRadius(4),
                Background = TintBrush(32),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = "当前工作区",
                    FontSize = 10.5 * _scale,
                    Foreground = (Brush)Resources["DialogAccentBrush"]
                }
            });
        }

        var panel = new StackPanel();
        panel.Children.Add(title);
        panel.Children.Add(new TextBlock
        {
            Text = DescribeWorkspace(workspace),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11.5 * _scale,
            Foreground = (Brush)Resources["DialogWeakBrush"],
            Margin = new Thickness(0, 3, 0, 0)
        });
        return panel;
    }

    /// <summary>与宿主待办勾选框一致：圆角方框、选中后填充强调色并显示对勾。</summary>
    private Style WorkspaceCheckBoxStyle()
    {
        if (_workspaceCheckBoxStyle is not null)
        {
            return _workspaceCheckBoxStyle;
        }

        var size = 16.0 * _scale;
        var style = new Style(typeof(CheckBox));
        style.Setters.Add(new Setter(Control.CursorProperty, Cursors.Hand));
        style.Setters.Add(new Setter(Control.FocusVisualStyleProperty, null));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        style.Setters.Add(new Setter(UIElement.SnapsToDevicePixelsProperty, true));

        var root = new FrameworkElementFactory(typeof(DockPanel));
        root.SetValue(DockPanel.LastChildFillProperty, true);

        var checkGrid = new FrameworkElementFactory(typeof(Grid));
        checkGrid.SetValue(FrameworkElement.WidthProperty, size);
        checkGrid.SetValue(FrameworkElement.HeightProperty, size);
        checkGrid.SetValue(DockPanel.DockProperty, Dock.Left);
        checkGrid.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "CheckBorder";
        border.SetValue(FrameworkElement.WidthProperty, size);
        border.SetValue(FrameworkElement.HeightProperty, size);
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1.5 * _scale));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4.0 * _scale));
        border.SetValue(
            Border.BorderBrushProperty,
            new DynamicResourceExtension("DialogWeakBrush"));
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        border.SetValue(UIElement.SnapsToDevicePixelsProperty, true);
        checkGrid.AppendChild(border);

        var path = new FrameworkElementFactory(typeof(System.Windows.Shapes.Path));
        path.Name = "CheckMark";
        path.SetValue(
            System.Windows.Shapes.Path.DataProperty,
            Geometry.Parse("M 3,7.5 L 6.5,11 L 13,4"));
        path.SetValue(
            System.Windows.Shapes.Path.StrokeProperty,
            new DynamicResourceExtension("DialogPaperBrush"));
        path.SetValue(System.Windows.Shapes.Path.StrokeThicknessProperty, 2.0);
        path.SetValue(
            System.Windows.Shapes.Path.StrokeStartLineCapProperty,
            PenLineCap.Round);
        path.SetValue(
            System.Windows.Shapes.Path.StrokeEndLineCapProperty,
            PenLineCap.Round);
        path.SetValue(
            System.Windows.Shapes.Path.StrokeLineJoinProperty,
            PenLineJoin.Round);
        path.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
        path.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        path.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        path.SetValue(
            FrameworkElement.LayoutTransformProperty,
            new ScaleTransform(_scale, _scale));
        checkGrid.AppendChild(path);
        root.AppendChild(checkGrid);

        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.MarginProperty, new Thickness(10.0 * _scale, 0, 0, 0));
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        root.AppendChild(content);

        var template = new ControlTemplate(typeof(CheckBox))
        {
            VisualTree = root
        };

        var checkedTrigger = new Trigger
        {
            Property = ToggleButton.IsCheckedProperty,
            Value = true
        };
        checkedTrigger.Setters.Add(new Setter(
            Border.BackgroundProperty,
            new DynamicResourceExtension("DialogAccentBrush"),
            "CheckBorder"));
        checkedTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, Brushes.Transparent, "CheckBorder"));
        checkedTrigger.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(0), "CheckBorder"));
        checkedTrigger.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "CheckMark"));

        var hoverUnchecked = new MultiTrigger();
        hoverUnchecked.Conditions.Add(new Condition
        {
            Property = UIElement.IsMouseOverProperty,
            Value = true
        });
        hoverUnchecked.Conditions.Add(new Condition
        {
            Property = ToggleButton.IsCheckedProperty,
            Value = false
        });
        hoverUnchecked.Setters.Add(new Setter(
            Border.BorderBrushProperty,
            new DynamicResourceExtension("DialogAccentBrush"),
            "CheckBorder"));
        hoverUnchecked.Setters.Add(new Setter(
            Border.BackgroundProperty,
            new DynamicResourceExtension("DialogButtonHoverBrush"),
            "CheckBorder"));

        var hoverChecked = new MultiTrigger();
        hoverChecked.Conditions.Add(new Condition
        {
            Property = UIElement.IsMouseOverProperty,
            Value = true
        });
        hoverChecked.Conditions.Add(new Condition
        {
            Property = ToggleButton.IsCheckedProperty,
            Value = true
        });
        hoverChecked.Setters.Add(new Setter(
            Border.BackgroundProperty,
            new DynamicResourceExtension("DialogAccentBrush"),
            "CheckBorder"));
        hoverChecked.Setters.Add(new Setter(Border.BorderBrushProperty, Brushes.Transparent, "CheckBorder"));
        hoverChecked.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(0), "CheckBorder"));

        template.Triggers.Add(checkedTrigger);
        template.Triggers.Add(hoverUnchecked);
        template.Triggers.Add(hoverChecked);

        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        _workspaceCheckBoxStyle = style;
        return style;
    }

    private string DescribeWorkspace(CoreBackupWorkspace workspace)
    {
        if (!workspace.IsReadable)
        {
            return "无法读取这个工作区文件。";
        }

        var text = $"工作区 {workspace.WorkspaceNumber} · 可导入 {workspace.PaperCount} 张" +
                   $"（{workspace.NoteCount} 笔记 · {workspace.TodoCount} 待办）";
        if (workspace.SkippedPluginBodies > 0)
        {
            text += $"；跳过 {workspace.SkippedPluginBodies} 张插件正文";
        }

        return text;
    }

    private void RefreshWorkspaceSelection()
    {
        if (_primaryButton is not null)
        {
            _primaryButton.IsEnabled =
                _workspaceChoices.Any(choice => choice.Box.IsChecked == true);
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
        bool canCreate,
        int newPaperCount = 0,
        IReadOnlyList<string>? unchangedTitles = null,
        IReadOnlyList<string>? warnings = null)
    {
        // 固定宽度，差异文本在剩余空间内自动换行。
        Width = 660;
        MinWidth = 660;
        MaxWidth = 660;
        _newPaperCount = Math.Max(0, newPaperCount);
        _unchangedTitles = unchangedTitles ?? [];

        var intro =
            "以下纸片匹配到本地已有内容。默认全部“跳过”，只有显式选择“更新”的纸片才会被覆盖。";
        if (_newPaperCount > 0)
        {
            intro += $"本次还会新建 {_newPaperCount} 张未命中本地的纸片。";
        }

        if (_unchangedTitles.Count > 0)
        {
            intro += $"另有 {_unchangedTitles.Count} 张纸片内容没有任何变化，已从下方省略（按跳过处理）。";
        }

        BodyPanel.Children.Add(new TextBlock
        {
            Text = intro,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12.5 * _scale,
            Foreground = (Brush)Resources["DialogWeakBrush"],
            Margin = new Thickness(0, 0, 0, 8)
        });

        if (_unchangedTitles.Count > 0)
        {
            BodyPanel.Children.Add(BuildUnchangedPanel());
        }

        if (unrecoverable is { Count: > 0 })
        {
            BodyPanel.Children.Add(BuildUnrecoverablePanel(unrecoverable));
        }

        if (warnings is { Count: > 0 })
        {
            BodyPanel.Children.Add(BuildWarningsPanel(warnings));
        }

        BodyPanel.Children.Add(BuildDecisionToolbar());

        for (var index = 0; index < papers.Count; index++)
        {
            BodyPanel.Children.Add(BuildUpdateCard(papers[index], index, canCreate));
        }

        RefreshUpdateDecision();
    }

    /// <summary>被省略的“内容无变化”纸片标题，让用户明确知道哪些没有出现在差异列表里。</summary>
    private FrameworkElement BuildUnchangedPanel()
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = $"内容无变化的纸片（{_unchangedTitles.Count} 张）：",
            FontSize = 11.5 * _scale,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)Resources["DialogWeakBrush"],
            Margin = new Thickness(0, 0, 0, 3)
        });

        foreach (var title in _unchangedTitles)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "· " + title,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11.5 * _scale,
                Foreground = (Brush)Resources["DialogWeakBrush"],
                Margin = new Thickness(0, 0, 0, 2)
            });
        }

        return new Border
        {
            Background = TintBrush(14),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 7, 10, 7),
            Margin = new Thickness(0, 0, 0, 10),
            Child = panel
        };
    }

    /// <summary>更新预览顶部的批量操作与实时摘要。</summary>
    private FrameworkElement BuildDecisionToolbar()
    {
        _updateSummaryText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12.0 * _scale,
            Foreground = (Brush)Resources["DialogTextBrush"],
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };

        var updateAll = CreateMiniButton("全部更新");
        updateAll.Click += (_, _) => SetAllChoices(ImportUpdateAction.Update);
        var skipAll = CreateMiniButton("全部跳过");
        skipAll.Margin = new Thickness(6, 0, 0, 0);
        skipAll.Click += (_, _) => SetAllChoices(ImportUpdateAction.Skip);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { updateAll, skipAll }
        };

        var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(buttons, 1);
        grid.Children.Add(_updateSummaryText);
        grid.Children.Add(buttons);
        return grid;
    }

    private Button CreateMiniButton(string text)
    {
        var button = CreateButton(text, TintBrush(28));
        button.MinWidth = 0;
        button.Padding = new Thickness(10, 4, 10, 4);
        button.FontSize = 11.5 * _scale;
        return button;
    }

    private void SetAllChoices(ImportUpdateAction action)
    {
        foreach (var choices in _choiceButtons.Values)
        {
            if (choices.TryGetValue(action, out var button) && button.IsEnabled)
            {
                button.IsChecked = true;
            }
        }
    }

    /// <summary>按逐张选择实时更新摘要与主按钮文案，避免“继续导入”实际什么都不做。</summary>
    private void RefreshUpdateDecision()
    {
        var update = _updateChoices.Count(pair => pair.Value == ImportUpdateAction.Update);
        var create = _updateChoices.Count(pair => pair.Value == ImportUpdateAction.CreateNew);
        var skip = _updateChoices.Count(pair => pair.Value == ImportUpdateAction.Skip);
        var totalCreate = create + _newPaperCount;

        if (_updateSummaryText != null)
        {
            var summary = $"将更新 {update} 张 · 新建 {totalCreate} 张 · 跳过 {skip} 张";
            if (_unchangedTitles.Count > 0)
            {
                summary += $" · 省略 {_unchangedTitles.Count} 张无变化";
            }

            _updateSummaryText.Text = summary;
        }

        if (_primaryButton is null)
        {
            return;
        }

        // 全部跳过也是一个有效决定：允许继续，导入结果会显示“已跳过 N 张”。
        // 不允许禁用主按钮，否则用户会被困在预览窗口里。
        var actionable = update + create;
        _primaryButton.IsEnabled = true;
        _primaryButton.Content = actionable > 0
            ? $"导入（更新 {update} · 新建 {totalCreate}）"
            : "全部跳过并关闭";
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
                Text = "文件无法完整还原正文引用的部分图片（缺失、校验失败或无法压缩到 8 MiB 以内）；选择“更新”会移除这些引用，其余正文仍会更新。",
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

        // 图片失败不再阻止更新：更新会移除失败引用，但保留文字更新。
        var updateAllowed = !contentUnavailable;
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

        card.Children.Add(BuildDiffPanel(paper.UpdateDiff, paper.UpdateHasMetaChanges));
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
        if (!_choiceButtons.TryGetValue(paper, out var choices))
        {
            choices = [];
            _choiceButtons[paper] = choices;
        }

        choices[action] = radio;
        radio.Checked += (_, _) =>
        {
            _updateChoices[paper] = action;
            RefreshUpdateDecision();
        };
        host.Children.Add(radio);
    }

    private FrameworkElement BuildDiffPanel(
        IReadOnlyList<DiffLine>? diff,
        bool hasMetaChanges = false)
    {
        var panel = new StackPanel();
        var mono = new FontFamily("Consolas, Microsoft YaHei UI");

        // 只有出现真正的增/删行才算有变化；省略行与相同行不应被当成差异。
        if (diff is not { Count: > 0 } ||
            diff.All(line => line.Kind is not ('-' or '+')))
        {
            panel.Children.Add(new TextBlock
            {
                Text = hasMetaChanges
                    ? "（正文/待办内容无变化，但顺序、提醒或关联纸片与本地不同）"
                    : "（内容无变化）",
                TextWrapping = TextWrapping.Wrap,
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
             node = DependencyTree.GetParent(node))
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
        Resources["DialogAccentBrush"] = MakeBrush(theme.AccentColor, Color.FromRgb(0x2F, 0x6B, 0xFF));
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
        _primaryButton = primary;
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
