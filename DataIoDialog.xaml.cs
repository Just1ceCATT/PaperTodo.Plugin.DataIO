using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PaperTodo.Plugin;

namespace PaperTodo.Plugin.DataIo;

public enum DataIoDialogIcon
{
    Question,
    Success,
    Warning,
    Error
}

/// <summary>与插件主题一致的自定义模态对话框，替代系统 MessageBox。</summary>
public partial class DataIoDialog : Window
{
    private const double ContentMaxWidth = 470;
    private readonly double _scale;

    public DataIoDialog(
        PaperBodyTheme theme,
        string title,
        DataIoDialogIcon icon,
        string primaryText,
        string? secondaryText = null)
    {
        InitializeComponent();

        _scale = theme.FontScale <= 0 ? 1.0 : Math.Clamp(theme.FontScale, 0.8, 2.4);
        ApplyTheme(theme);

        TitleText.Text = title;
        TitleText.FontSize = 17.0 * _scale;
        IconText.Text = Glyph(icon);
        IconText.FontSize = 18.0 * _scale;

        BuildButtons(primaryText, secondaryText);
        CloseButton.Click += (_, _) => CloseWith(false);
        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>消息型内容：自动换行的多行文本。</summary>
    public void SetMessage(string message)
    {
        BodyPanel.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = ContentMaxWidth,
            LineHeight = 21.0 * _scale,
            FontSize = 13.5 * _scale,
            Foreground = (Brush)Resources["DialogTextBrush"]
        });
    }

    /// <summary>结果型内容：标题 + 统计行 + 可选详情。</summary>
    public void SetStats(
        string headline,
        IReadOnlyList<(string Label, string Value)> stats,
        string? details)
    {
        BodyPanel.Children.Add(new TextBlock
        {
            Text = headline,
            Margin = new Thickness(0, 0, 0, 10),
            FontSize = 15.0 * _scale,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)Resources["DialogTextBrush"]
        });

        var rows = new StackPanel();
        foreach (var (label, value) in stats)
        {
            var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
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
            Background = (Brush)Resources["DialogSurfaceBrush"],
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 8, 12, 8),
            Child = rows
        });

        if (!string.IsNullOrWhiteSpace(details))
        {
            BodyPanel.Children.Add(new TextBlock
            {
                Text = details,
                Margin = new Thickness(0, 10, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = ContentMaxWidth,
                LineHeight = 18.0 * _scale,
                FontSize = 12.0 * _scale,
                Foreground = (Brush)Resources["DialogWeakBrush"]
            });
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
        Resources["DialogAccentBrush"] = MakeBrush(theme.AccentColor, Color.FromRgb(0x2F, 0x6B, 0xFF));
        Resources["DialogBorderBrush"] = MakeBrush(theme.BorderColor, Color.FromRgb(0x3A, 0x3A, 0x3A));
        Resources["DialogPaperBrush"] = MakeBrush(
            theme.PaperColor,
            theme.IsDark ? Color.FromRgb(0x24, 0x24, 0x24) : Colors.White);
        Resources["DialogSurfaceBrush"] = MakeBrush(
            theme.IsDark ? "#1AFFFFFF" : "#0A000000",
            Color.FromArgb(0x0A, 0x00, 0x00, 0x00));
        Resources["DialogHoverBrush"] = MakeBrush(
            theme.IsDark ? "#1FFFFFFF" : "#0F000000",
            Color.FromArgb(0x0F, 0x00, 0x00, 0x00));

        var accent = (SolidColorBrush)Resources["DialogAccentBrush"];
        var accentSurface = new SolidColorBrush(Color.FromArgb(
            theme.IsDark ? (byte)46 : (byte)26,
            accent.Color.R,
            accent.Color.G,
            accent.Color.B));
        accentSurface.Freeze();
        Resources["DialogAccentSurfaceBrush"] = accentSurface;

        FontFamily = new FontFamily(string.IsNullOrWhiteSpace(theme.FontFamily)
            ? "Microsoft YaHei UI"
            : theme.FontFamily);
        FontSize = 13.5 * _scale;
    }

    private void BuildButtons(string primaryText, string? secondaryText)
    {
        var text = (Brush)Resources["DialogTextBrush"];
        var accent = (Brush)Resources["DialogAccentBrush"];
        var border = (Brush)Resources["DialogBorderBrush"];

        if (!string.IsNullOrWhiteSpace(secondaryText))
        {
            var secondary = CreateButton(secondaryText!, text, Brushes.Transparent, border);
            secondary.Margin = new Thickness(0, 0, 8, 0);
            secondary.Click += (_, _) => CloseWith(false);
            ButtonPanel.Children.Add(secondary);
        }

        var primary = CreateButton(primaryText, Brushes.White, accent, accent);
        primary.Click += (_, _) => CloseWith(true);
        ButtonPanel.Children.Add(primary);
    }

    private Button CreateButton(string text, Brush foreground, Brush background, Brush border)
    {
        return new Button
        {
            Content = text,
            Style = (Style)FindResource("DialogButton"),
            FontSize = 13.0 * _scale,
            Foreground = foreground,
            Background = background,
            BorderBrush = border
        };
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
        else if (e.Key == Key.Enter)
        {
            CloseWith(true);
            e.Handled = true;
        }
    }

    private static string Glyph(DataIoDialogIcon icon) => icon switch
    {
        DataIoDialogIcon.Success => "✓",
        DataIoDialogIcon.Warning => "!",
        DataIoDialogIcon.Error => "!",
        _ => "?"
    };

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
