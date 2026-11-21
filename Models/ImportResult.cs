namespace PaperTodo.Plugin.DataIo;

/// <summary>一次导入操作的结果汇总。导入是逐张纸片的部分成功语义。</summary>
internal sealed class ImportResult
{
    public int CreatedPapers { get; set; }
    public int FailedPapers { get; set; }
    public int CreatedTodos { get; set; }
    public int RestoredLinks { get; set; }
    public int RestoredReminders { get; set; }

    public List<string> Warnings { get; } = [];
    public List<ImportFailure> Failures { get; } = [];
    public List<string> DroppedPathLinkNotes { get; } = [];

    /// <summary>非空表示整次导入被中止。</summary>
    public string? FatalError { get; set; }

    /// <summary>是否因为会话失效、容量上限等严重错误提前停止。</summary>
    public bool StoppedEarly { get; set; }

    public bool HasProblems =>
        FailedPapers > 0 || Warnings.Count > 0 || FatalError is not null;
}

internal sealed record ImportFailure(
    int Index,
    string Title,
    string Type,
    string Code,
    string Message);
