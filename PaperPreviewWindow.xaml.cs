using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using PaperTodo.Plugin;

namespace PaperTodo.Plugin.DataIo;

/// <summary>
/// 与宿主 PaperNoticeDialog 同款视觉的预览窗口：顶部元信息、可滚动正文、
/// 底部上下条导航与“加入/移出导出（导入）”快捷操作。
/// 正文按需异步加载，窗口本身不直接访问宿主 API。
/// </summary>
internal partial class PaperPreviewWindow : Window
{
    private readonly double _scale;
    private readonly Color _tint;
    private readonly DataIoMode _mode;
    private readonly IReadOnlyList<string> _keys;
    private readonly Func<string, Task<PreviewContent?>> _load;
    private readonly Func<string, bool> _canSelect;
    private readonly Func<string, bool> _isSelected;
    private readonly Action<string, bool> _setSelected;

    private int _index;
    private bool _loading;
    private int _loadToken;
    private bool _closeAnimated;
    private PreviewContent? _current;

    public PaperPreviewWindow(
        PaperBodyTheme theme,
        Color tint,
        DataIoMode mode,
        IReadOnlyList<string> keys,
        int index,
        Func<string, Task<PreviewContent?>> load,
        Func<string, bool> canSelect,
        Func<string, bool> isSelected,
        Action<string, bool> setSelected)
    {
        InitializeComponent();

        _scale = theme.FontScale <= 0 ? 1.0 : Math.Clamp(theme.FontScale, 0.8, 2.4);
        _tint = tint;
        _mode = mode;
        _keys = keys;
        _index = Math.Clamp(index, 0, Math.Max(0, keys.Count - 1));
        _load = load;
        _canSelect = canSelect;
        _isSelected = isSelected;
        _setSelected = setSelected;

        ApplyTheme(theme);
        SizeToWorkArea();

        PrevButton.Click += (_, _) => Move(-1);
        NextButton.Click += (_, _) => Move(1);
        CopyButton.Click += OnCopyClick;
        SelectButton.Click += OnSelectClick;
        DoneButton.Click += (_, _) => Close();
        CloseIconButton.Click += (_, _) => Close();
        PreviewKeyDown += OnPreviewKeyDown;

        // 无标题栏窗口：标题区域和空白区域可用于拖动。
        TitleText.MouseLeftButtonDown += OnDragAreaMouseDown;
        Shell.MouseLeftButtonDown += OnShellMouseDown;

        // 首帧先隐藏，等 Loaded 后再做淡入缩放，避免窗口出现时闪一下。
        if (AnimationsEnabled)
        {
            Shell.Opacity = 0;
            ShellScale.ScaleX = 0.98;
            ShellScale.ScaleY = 0.98;
        }

        Loaded += OnLoaded;
        UpdateNav();
        _ = LoadCurrentAsync();
    }

    private static bool AnimationsEnabled => SystemParameters.ClientAreaAnimation;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Shell.Opacity = 1;
        ShellScale.ScaleX = 1;
        ShellScale.ScaleY = 1;

        if (!AnimationsEnabled)
        {
            return;
        }

        Shell.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(170))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            });
        ShellScale.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.98, 1, TimeSpan.FromMilliseconds(190))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            });
        ShellScale.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.98, 1, TimeSpan.FromMilliseconds(190))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            });
    }

    /// <summary>关闭前先做淡出收缩；动画完成后再次 Close，第二次直接放行。</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_closeAnimated && AnimationsEnabled && IsLoaded)
        {
            _closeAnimated = true;
            e.Cancel = true;

            var fade = new DoubleAnimation(Shell.Opacity, 0, TimeSpan.FromMilliseconds(110))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
                FillBehavior = FillBehavior.HoldEnd
            };
            fade.Completed += (_, _) => Close();

            var scaleX = new DoubleAnimation(ShellScale.ScaleX, 0.98, TimeSpan.FromMilliseconds(110))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
                FillBehavior = FillBehavior.HoldEnd
            };
            var scaleY = new DoubleAnimation(ShellScale.ScaleY, 0.98, TimeSpan.FromMilliseconds(110))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
                FillBehavior = FillBehavior.HoldEnd
            };

            Shell.BeginAnimation(OpacityProperty, fade);
            ShellScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleX);
            ShellScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleY);
            return;
        }

        base.OnClosing(e);
    }

    private string? CurrentKey => _keys.Count == 0 ? null : _keys[_index];

    /// <summary>按宿主风格居中显示；宿主窗口不可见时退化为屏幕居中。</summary>
    public void ShowModal(Window? owner)
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

        ShowDialog();
    }

    private void SizeToWorkArea()
    {
        var work = SystemParameters.WorkArea;
        var widthFactor = Math.Clamp(_scale, 1.0, 1.45);
        Width = Math.Min(560 * widthFactor, work.Width * 0.9);
        Height = Math.Min(640 * Math.Clamp(_scale, 1.0, 1.25), work.Height * 0.85);
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
        Resources["DialogWarningBrush"] = MakeBrush(
            theme.IsDark ? "#FFD97B72" : "#FFC44B42",
            Color.FromRgb(0xC4, 0x4B, 0x42));
        Resources["DialogSurfaceBrush"] = MakeBrush(
            theme.IsDark ? "#14FFFFFF" : "#0C000000",
            Color.FromArgb(0x0C, 0x00, 0x00, 0x00));
        Resources["DialogTintBrush"] = TintBrush(32);
        Resources["DialogButtonHoverBrush"] = TintBrush(46);
        Resources["DialogIconFontFamily"] = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");

        var fontFamily = new FontFamily(string.IsNullOrWhiteSpace(theme.FontFamily)
            ? "Microsoft YaHei UI"
            : theme.FontFamily);
        FontFamily = fontFamily;
        FontSize = 12.0 * _scale;

        TitleText.FontSize = 18.0 * _scale;
        TypeText.FontSize = 10.5 * _scale;
        MetaText.FontSize = 11.5 * _scale;
        WarningText.FontSize = 11.5 * _scale;
        CounterText.FontSize = 11.5 * _scale;
        CloseIconButton.FontSize = 13.0 * _scale;
        PrevButton.FontSize = 13.0 * _scale;
        NextButton.FontSize = 13.0 * _scale;
        CopyButton.FontSize = 12.5 * _scale;
        SelectButton.FontSize = 12.5 * _scale;
        DoneButton.FontSize = 12.5 * _scale;
    }

    private PreviewPalette CreatePalette() => new(
        (Brush)Resources["DialogTextBrush"],
        (Brush)Resources["DialogWeakBrush"],
        (Brush)Resources["DialogBorderBrush"],
        (Brush)Resources["DialogAccentBrush"],
        (Brush)Resources["DialogSurfaceBrush"],
        _scale,
        FontFamily,
        new FontFamily("Cascadia Mono, Consolas, Courier New"));

    // ---------- 导航与加载 ----------

    private void Move(int delta)
    {
        if (_loading)
        {
            return;
        }

        var next = _index + delta;
        if (next < 0 || next >= _keys.Count)
        {
            return;
        }

        _index = next;
        _ = LoadCurrentAsync();
    }

    private async Task LoadCurrentAsync()
    {
        var token = ++_loadToken;
        var key = CurrentKey;
        _current = null;
        UpdateNav();
        UpdateSelectButton();

        if (key is null)
        {
            RenderEmpty("没有可预览的纸片。");
            return;
        }

        _loading = true;
        UpdateNav();
        ShowLoading();

        PreviewContent? content = null;
        try
        {
            content = await _load(key);
        }
        catch (Exception)
        {
            // 预览是辅助操作，读取失败时保留窗口并展示占位，不打断用户。
        }

        if (token != _loadToken)
        {
            return;
        }

        _loading = false;
        _current = content;
        UpdateNav();
        UpdateSelectButton();

        if (content is null)
        {
            RenderEmpty("无法读取这张纸片的内容。");
            return;
        }

        RenderContent(content);
    }

    private void UpdateNav()
    {
        if (!IsInitialized)
        {
            return;
        }

        PrevButton.IsEnabled = !_loading && _index > 0;
        NextButton.IsEnabled = !_loading && _index < _keys.Count - 1;
        CounterText.Text = _keys.Count == 0 ? "0 / 0" : $"{_index + 1} / {_keys.Count}";

        var hasText = _current is not null &&
                      (_current.Body.Length > 0 || _current.Todos.Count > 0);
        CopyButton.IsEnabled = !_loading && hasText;
        CopyButton.Content = "复制正文";
    }

    private void UpdateSelectButton()
    {
        if (!IsInitialized)
        {
            return;
        }

        var key = CurrentKey;
        var canSelect = key is not null && _canSelect(key);
        SelectButton.IsEnabled = !_loading && _current is not null && canSelect;

        var selected = key is not null && _isSelected(key);
        SelectButton.Content = _mode == DataIoMode.Export
            ? (selected ? "移出导出" : "加入导出")
            : (selected ? "移出导入" : "加入导入");
        SelectButton.Background = TintBrush(selected ? 56 : 32);
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

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
            case Key.Left:
                Move(-1);
                e.Handled = true;
                break;
            case Key.Right:
                Move(1);
                e.Handled = true;
                break;
        }
    }

    // ---------- 渲染 ----------

    private void ShowLoading()
    {
        BodyPanel.Children.Clear();
        BodyPanel.Children.Add(new TextBlock
        {
            Text = "正在读取…",
            FontSize = 12.5 * _scale,
            Foreground = (Brush)Resources["DialogWeakBrush"]
        });
        BodyScroll.ScrollToTop();
    }

    private void RenderEmpty(string message)
    {
        _current = null;
        TitleText.Text = "预览";
        TypeText.Text = "";
        MetaText.Text = "";
        WarningText.Visibility = Visibility.Collapsed;
        CounterText.Text = _keys.Count == 0 ? "0 / 0" : $"{_index + 1} / {_keys.Count}";
        BodyPanel.Children.Clear();
        BodyPanel.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12.5 * _scale,
            Foreground = (Brush)Resources["DialogWeakBrush"]
        });
        AnimateBodyIn();
        UpdateNav();
        UpdateSelectButton();
    }

    private void RenderContent(PreviewContent content)
    {
        TitleText.Text = content.Title;
        TypeText.Text = content.TypeLabel;
        MetaText.Text = content.Meta;

        if (string.IsNullOrWhiteSpace(content.Warning))
        {
            WarningText.Visibility = Visibility.Collapsed;
        }
        else
        {
            WarningText.Text = content.Warning;
            WarningText.Visibility = Visibility.Visible;
        }

        BodyPanel.Children.Clear();

        if (content.Todos.Count > 0)
        {
            foreach (var todo in content.Todos)
            {
                BodyPanel.Children.Add(BuildTodoRow(todo));
            }
        }
        else if (!content.ContentAvailable)
        {
            BodyPanel.Children.Add(new TextBlock
            {
                Text = "正文不可读取。",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12.5 * _scale,
                Foreground = (Brush)Resources["DialogWeakBrush"]
            });
        }
        else
        {
            MarkdownPreview.Render(content.Body, BodyPanel, CreatePalette());
        }

        AnimateBodyIn();
    }

    /// <summary>切换条目时让正文淡入并轻微上移，避免内容生硬跳变。</summary>
    private void AnimateBodyIn()
    {
        BodyScroll.ScrollToTop();
        BodyScroll.Opacity = 1;
        BodyTranslate.Y = 0;

        if (!AnimationsEnabled)
        {
            return;
        }

        BodyScroll.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0.35, 1, TimeSpan.FromMilliseconds(150))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            });
        BodyTranslate.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(6, 0, TimeSpan.FromMilliseconds(170))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            });
    }

    private Grid BuildTodoRow(PreviewTodoItem todo)
    {
        var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var box = new TextBlock
        {
            Text = todo.Done ? "" : "",
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Top,
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 13.0 * _scale,
            LineHeight = 20.0 * _scale,
            Foreground = todo.Done
                ? (Brush)Resources["DialogAccentBrush"]
                : (Brush)Resources["DialogWeakBrush"]
        };

        var textPanel = new StackPanel();
        textPanel.Children.Add(new TextBlock
        {
            Text = todo.Text,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13.0 * _scale,
            LineHeight = 20.0 * _scale,
            Opacity = todo.Done ? 0.62 : 1.0,
            Foreground = (Brush)Resources["DialogTextBrush"]
        });

        if (todo.HasMeta)
        {
            textPanel.Children.Add(new TextBlock
            {
                Text = todo.Meta,
                Margin = new Thickness(0, 2, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11.0 * _scale,
                Foreground = (Brush)Resources["DialogWeakBrush"]
            });
        }

        Grid.SetColumn(textPanel, 1);
        row.Children.Add(box);
        row.Children.Add(textPanel);
        return row;
    }

    // ---------- 操作 ----------

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (_current is null)
        {
            return;
        }

        var text = BuildCopyText(_current);
        if (text.Length == 0)
        {
            return;
        }

        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception)
        {
            // 剪贴板被其他进程占用时静默失败，不影响预览。
            return;
        }

        CopyButton.Content = "已复制";
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            CopyButton.Content = "复制正文";
        };
        timer.Start();
    }

    private void OnSelectClick(object sender, RoutedEventArgs e)
    {
        var key = CurrentKey;
        if (key is null || _current is null || !_canSelect(key))
        {
            return;
        }

        _setSelected(key, !_isSelected(key));
        UpdateSelectButton();
    }

    private string BuildCopyText(PreviewContent content)
    {
        if (content.Todos.Count == 0)
        {
            return content.Body;
        }

        var lines = content.Todos.Select(t =>
            (t.Done ? "- [x] " : "- [ ] ") + t.Text + (t.HasMeta ? $"（{t.Meta}）" : ""));
        return string.Join(Environment.NewLine, lines);
    }

    // ---------- 主题辅助 ----------

    private SolidColorBrush TintBrush(int alpha)
    {
        var brush = new SolidColorBrush(Color.FromArgb((byte)alpha, _tint.R, _tint.G, _tint.B));
        brush.Freeze();
        return brush;
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
