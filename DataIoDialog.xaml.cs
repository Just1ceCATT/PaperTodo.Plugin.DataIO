using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
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

    /// <summary>更新预览：逐张展示本地内容与导入内容的差异，并让用户选择跳过/更新/新增。</summary>
    internal void SetUpdatePreview(IReadOnlyList<ImportPaper> papers)
    {
        Width = 640;

        BodyPanel.Children.Add(new TextBlock
        {
            Text = "以下纸片将更新本地已有内容，请为每张纸片选择处理方式：",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12.5 * _scale,
            Foreground = (Brush)Resources["DialogWeakBrush"],
            Margin = new Thickness(0, 0, 0, 8)
        });

        for (var index = 0; index < papers.Count; index++)
        {
            BodyPanel.Children.Add(BuildUpdateCard(papers[index], index));
        }
    }

    private FrameworkElement BuildUpdateCard(ImportPaper paper, int index)
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
                Text = "本地内容自上次导入后被修改，更新会覆盖这些修改。",
                FontSize = 11.5 * _scale,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)Resources["DialogWarningBrush"],
                Margin = new Thickness(0, 3, 0, 0)
            });
        }

        var choices = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 6, 0, 5)
        };
        AddChoice(choices, paper, index, ImportUpdateAction.Skip, "跳过");
        AddChoice(choices, paper, index, ImportUpdateAction.Update, "更新");
        AddChoice(choices, paper, index, ImportUpdateAction.CreateNew, "新增");
        card.Children.Add(choices);

        card.Children.Add(BuildDiffPanel(paper.UpdateDiff));
        return card;
    }

    private void AddChoice(
        Panel host,
        ImportPaper paper,
        int index,
        ImportUpdateAction action,
        string label)
    {
        var radio = new RadioButton
        {
            Content = label,
            GroupName = $"DataIoUpdateChoice{index}",
            Style = (Style)FindResource("UpdateChoice"),
            FontSize = 12.0 * _scale,
            IsChecked = action == ImportUpdateAction.Update
        };
        _updateChoices[paper] = ImportUpdateAction.Update;
        radio.Checked += (_, _) => _updateChoices[paper] = action;
        host.Children.Add(radio);
    }

    private FrameworkElement BuildDiffPanel(IReadOnlyList<DiffLine>? diff)
    {
        var text = new TextBlock
        {
            FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"),
            FontSize = 11.5 * _scale,
            TextWrapping = TextWrapping.Wrap
        };

        if (diff is not { Count: > 0 } ||
            diff.All(line => line.Kind == ' '))
        {
            text.Inlines.Add(new Run("（内容无变化）")
            {
                Foreground = (Brush)Resources["DialogWeakBrush"]
            });
        }
        else
        {
            foreach (var line in diff)
            {
                var run = new Run(line.Kind switch
                {
                    '+' => "+ " + line.Text,
                    '-' => "- " + line.Text,
                    _ => "  " + line.Text
                })
                {
                    Foreground = (Brush)Resources[line.Kind switch
                    {
                        '+' => "DialogAddedBrush",
                        '-' => "DialogRemovedBrush",
                        _ => "DialogWeakBrush"
                    }]
                };
                text.Inlines.Add(run);
                text.Inlines.Add(new Run(Environment.NewLine));
            }
        }

        return new Border
        {
            Background = TintBrush(8),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(9, 7, 9, 7),
            Child = text
        };
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
        Resources["DialogAddedBrush"] = MakeBrush(
            theme.IsDark ? "#7FD99A" : "#1E7F45",
            Color.FromRgb(0x7F, 0xD9, 0x9A));
        Resources["DialogRemovedBrush"] = MakeBrush(
            theme.IsDark ? "#F08A80" : "#B3261E",
            Color.FromRgb(0xF0, 0x8A, 0x80));

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
