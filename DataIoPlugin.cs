using PaperTodo.Plugin;
namespace PaperTodo.Plugin.DataIo;

/// <summary>
/// 插件入口。 PaperTodo 规定入口程序集内必须恰好有一个公开、非抽象且带公共无参构造函数的
/// IPaperBodyPlugin 实现，本类型即该唯一入口。
/// 同时实现 provider Runtime，用按工作区隔离的 Runtime state 持久化导入映射。
/// </summary>
public sealed class DataIoPlugin : IPaperBodyPlugin, IPaperPluginRuntimeProvider
{
    public string MigrateState(string stateJson, int fromVersion) =>
        DataIoState.Migrate(stateJson, fromVersion);

    public IPaperBodySession Create(PaperBodyContext context) => new DataIoSession(context);

    public IPaperPluginRuntime CreatePluginRuntime(PaperPluginRuntimeContext context) =>
        new DataIoRuntime(context);
}
