namespace PaperTodo.Plugin.DataIo;

/// <summary>一次导入操作的结果汇总。导入是逐张纸片的部分成功语义。</summary>
internal sealed class ImportResult
{
    public int CreatedPapers { get; set; }
    public int UpdatedPapers { get; set; }
    public int SkippedPapers { get; set; }
    public int FailedPapers { get; set; }
    public int CreatedTodos { get; set; }
    public int UpdatedTodos { get; set; }
    public int DeletedTodos { get; set; }
    public int RestoredLinks { get; set; }
    public int RestoredReminders { get; set; }

    /// <summary>成功处理的 sourceId → 本地纸片 ID，用于导入后更新身份映射。</summary>
    public Dictionary<string, string> SourceIdToPaperId { get; } =
        new(StringComparer.Ordinal);

    public List<string> Warnings { get; } = [];
    public List<ImportFailure> Failures { get; } = [];
    public List<string> DroppedPathLinkNotes { get; } = [];
    public List<string> SkippedTitles { get; } = [];

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
