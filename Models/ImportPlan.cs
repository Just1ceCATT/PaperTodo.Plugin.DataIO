namespace PaperTodo.Plugin.DataIo;

/// <summary>解析并校验后的导入计划，所有数据都在内存中，不引用宿主对象。</summary>
internal sealed class ImportPlan
{
    public List<ImportPaper> Papers { get; } = [];
    public List<string> Warnings { get; } = [];
    public int TodoCount { get; set; }
    public int TruncatedTitles { get; set; }
    public int UnresolvedLinks { get; set; }
    public int DroppedReminders { get; set; }
    public int PathLinkCount { get; set; }

    /// <summary>含图片引用的笔记张数与图片引用总数；图片本体不在导出文件中。</summary>
    public int NotesWithImages { get; set; }
    public int ImageReferenceCount { get; set; }

    /// <summary>正文含“图片缺失”标记的笔记张数（通常来自旧版 Markdown 导出）。</summary>
    public int LossyImageNotes { get; set; }

    /// <summary>非空表示文件整体不可导入。</summary>
    public string? FatalError { get; set; }

    public bool IsUsable => FatalError is null && Papers.Count > 0;
}

internal sealed class ImportPaper
{
    public required ExportPaper Source { get; init; }

    /// <summary>已清洗并预截断的标题；null 表示交给宿主生成默认标题。</summary>
    public string? SafeTitle { get; init; }

    /// <summary>命中身份映射时的本地更新目标；null 表示新建。</summary>
    public string? UpdateTargetId { get; set; }
    public string? UpdateTargetTitle { get; set; }

    /// <summary>命中的本地纸片自上次导入后被修改过。</summary>
    public bool TargetLocallyModified { get; set; }

    /// <summary>
    /// 导入正文含旧版“图片缺失”标记，而本地纸片仍含有图片引用。
    /// 此时更新会用标记文本覆盖本地图片引用，必须阻止。
    /// </summary>
    public bool TargetImagesAtRisk { get; set; }

    /// <summary>本地现有内容与导入内容的差异，供更新预览展示。</summary>
    public IReadOnlyList<DiffLine>? UpdateDiff { get; set; }

    /// <summary>
    /// 命中更新目标时，内容是否与本地真正不同。笔记按“正文等价”判断（忽略尾部空行与分隔线）；
    /// 为 false 时无需出现在更新预览里，也不应计入更新。
    /// </summary>
    public bool UpdateHasChanges { get; set; }
}
