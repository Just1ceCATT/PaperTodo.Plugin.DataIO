namespace PaperTodo.Plugin.DataIo;

/// <summary>PaperTodo 备份压缩包内的一个工作区快照及其可导入规模。</summary>
internal sealed record CoreBackupWorkspace(
    int WorkspaceNumber,
    string Name,
    int PaperCount,
    int NoteCount,
    int TodoCount,
    int SkippedPluginBodies,
    bool IsReadable);
