using System.Windows;
using System.Windows.Controls;
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
