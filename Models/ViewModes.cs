namespace PaperTodo.Plugin.DataIo;

/// <summary>工具纸片的当前操作模式。</summary>
public enum DataIoMode
{
    Export,
    Import,

    /// <summary>只读历史页：查看本机保存的最近导入/导出操作。</summary>
    History
}

/// <summary>导出文件格式，由用户在纸片内选择。</summary>
public enum DataIoExportFormat
{
    Json,
    Markdown
}

/// <summary>更新预览中，用户对单张纸片选择的处理方式。</summary>
public enum ImportUpdateAction
{
    Skip,
    Update,
    CreateNew
}
