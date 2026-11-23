using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace PaperTodo.Plugin.DataIo;

/// <summary>
/// 依赖对象父级遍历。Visual 走可视化树；Run 等 ContentElement 不属于可视化树，
/// 直接对它们调用 VisualTreeHelper.GetParent 会抛 InvalidOperationException，
/// 必须改走内容/逻辑树。
/// </summary>
internal static class DependencyTree
{
    public static DependencyObject? GetParent(DependencyObject node)
    {
        if (node is Visual or Visual3D)
        {
            return VisualTreeHelper.GetParent(node);
        }

        if (node is ContentElement content)
        {
            return ContentOperations.GetParent(content)
                   ?? (content as FrameworkContentElement)?.Parent;
        }

        return null;
    }
}
