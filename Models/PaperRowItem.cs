using System.ComponentModel;
using System.Windows;

namespace PaperTodo.Plugin.DataIo;

/// <summary>详情列表中的一行，承载勾选状态与展示摘要。</summary>
public sealed class PaperRowItem : INotifyPropertyChanged
{
    private bool _isSelected;

    /// <summary>稳定标识：导出时为宿主纸片 ID，导入时为计划内序号。</summary>
    public required string Key { get; init; }
    public required string Title { get; init; }
    /// <summary>宿主类型：todo 或 note。</summary>
    public required string Type { get; init; }
    public required string TypeLabel { get; init; }
    public string Summary { get; init; } = "";
    public string Warning { get; init; } = "";
    public bool CanSelect { get; init; } = true;

    /// <summary>
    /// 是否在行顶绘制分割线。每组的第一行为 false，避免与上方按钮区重复分隔，
    /// 最后一行不画底线，由列表外层收尾线统一分隔。
    /// 视图必须在把行加入绑定集合之前设置该值（本属性不触发变更通知）。
    /// </summary>
    public bool ShowTopSeparator { get; set; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public Visibility WarningVisibility =>
        string.IsNullOrEmpty(Warning) ? Visibility.Collapsed : Visibility.Visible;

    public event PropertyChangedEventHandler? PropertyChanged;
}
