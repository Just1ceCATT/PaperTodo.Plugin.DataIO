namespace PaperTodo.Plugin.DataIo;

/// <summary>工具纸片的当前操作模式。</summary>
public enum DataIoMode
{
    Export,
    Import
}

/// <summary>导出/导入的范围：全部数据或用户指定的纸片。</summary>
public enum DataIoScope
{
    All,
    Custom
}
