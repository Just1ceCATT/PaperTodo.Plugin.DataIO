using System.Windows.Threading;
using PaperTodo.Plugin;

namespace PaperTodo.Plugin.DataIo;

/// <summary>
/// 宿主 API 的统一调用入口：
/// 1) 集中判断权限；2) 所有宿主数据调用都切回 WPF UI 线程。
/// 宿主 PaperBodyPluginHostApi 的多数方法不自行切换线程，后台线程直接调用会破坏宿主状态。
/// </summary>
internal sealed class HostGateway
{
    private readonly PaperBodyContext _context;
    private readonly Dispatcher _dispatcher;

    public HostGateway(PaperBodyContext context, Dispatcher dispatcher)
    {
        _context = context;
        _dispatcher = dispatcher;
    }

    public PaperBodyContext Context => _context;

    public bool Can(string permission) => _context.GrantedPermissions.Contains(permission);

    public Task<T> OnUiAsync<T>(Func<T> action)
    {
        if (_dispatcher.CheckAccess())
        {
            try
            {
                return Task.FromResult(action());
            }
            catch (Exception ex)
            {
                return Task.FromException<T>(ex);
            }
        }

        return _dispatcher.InvokeAsync(action).Task;
    }

    public Task OnUiAsync(Action action) => OnUiAsync(() =>
    {
        action();
        return true;
    });

    /// <summary>把控制权交还给 Dispatcher，保持窗口可拖动、状态栏可刷新。</summary>
    public static async Task YieldAsync() =>
        await Dispatcher.Yield(DispatcherPriority.Background);
}
