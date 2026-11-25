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

    /// <summary>含图片引用的笔记张数与图片引用总数。</summary>
    public int NotesWithImages { get; set; }
    public int ImageReferenceCount { get; set; }

    /// <summary>压缩包中随文件提供的图片总数（含无法写回的超限图片）。</summary>
    public int ImageAssetCount { get; set; }

    /// <summary>可以写回笔记的图片数量。</summary>
    public int RestorableImageCount { get; set; }

    /// <summary>超过插件追加上限、且无法压缩到 8 MiB 以内，只能保留在压缩包中的图片数量。</summary>
    public int UnrestorableImageCount { get; set; }

    /// <summary>超过 8 MiB、已按宿主规则压缩后写回的图片数量。</summary>
    public int CompressedImageCount { get; set; }

    /// <summary>超过 8 MiB 且无法安全压缩的图片数量（GIF/TIFF、解码失败等）。</summary>
    public int CompressionFailedImageCount { get; set; }

    /// <summary>清单存在但文件缺失、损坏或校验失败的图片数量。</summary>
    public int MissingImageAssetCount { get; set; }

    /// <summary>正文中有引用、但压缩包没有提供对应图片的数量。</summary>
    public int UnresolvedImageReferenceCount { get; set; }

    /// <summary>压缩包内有、但没有任何正文引用的图片数量。</summary>
    public int UnreferencedImageAssetCount { get; set; }

    /// <summary>正文含“图片缺失”标记的笔记张数（通常来自旧版 Markdown 导出）。</summary>
    public int LossyImageNotes { get; set; }

    public bool HasEmbeddedImages => ImageAssetCount > 0;

    /// <summary>文件格式是否携带提醒数据；Markdown 不携带，更新时不能据此删除本地提醒。</summary>
    public bool SupportsReminders { get; set; } = true;

    /// <summary>文件格式是否携带待办关联纸片；Markdown 不携带，更新时不能据此清除本地关联。</summary>
    public bool SupportsPaperLinks { get; set; } = true;

    /// <summary>压缩包内的主文件类型，例如“JSON 压缩包”；普通文件为 null。</summary>
    public string? ContainerFormat { get; set; }

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
    /// 解析更新目标时本地纸片的内容指纹。写入前会重新计算并比对，
    /// 防止确认期间本地被修改后仍被静默覆盖。
    /// </summary>
    public string? UpdateTargetFingerprint { get; set; }

    /// <summary>
    /// 命中更新目标时，内容是否与本地真正不同。笔记按“正文等价”判断（忽略尾部空行与分隔线）；
    /// 为 false 时无需出现在更新预览里，也不应计入更新。
    /// </summary>
    public bool UpdateHasChanges { get; set; }

    /// <summary>
    /// 正文/待办文本没有差异，但顺序、提醒或关联纸片与本地不同。
    /// 这类纸片仍需出现在更新预览里，供用户选择是否同步。
    /// </summary>
    public bool UpdateHasMetaChanges { get; set; }

    /// <summary>随压缩包提供、属于这张笔记的图片。</summary>
    public List<ImportImageAsset> Images { get; } = [];

    /// <summary>压缩包正文使用 images/ 相对链接（Markdown 包），否则使用 i: 引用。</summary>
    public bool UsesRelativeImageLinks { get; set; }

    /// <summary>正文中能从压缩包找到图片的引用数量。</summary>
    public int ResolvedImageReferences { get; set; }

    /// <summary>正文中找不到对应图片的引用数量。</summary>
    public int UnresolvedImageReferences { get; set; }
}

/// <summary>压缩包内一张可写回笔记的图片；内容完全在内存中。</summary>
internal sealed class ImportImageAsset
{
    /// <summary>导出机器上的图片 ID，用于 JSON 包的 i: 引用映射。</summary>
    public required string SourceImageId { get; init; }

    /// <summary>压缩包内的相对路径，用于 Markdown 包的相对链接映射。</summary>
    public required string File { get; init; }

    public required string Mime { get; init; }
    public required byte[] Bytes { get; init; }

    /// <summary>false 表示超过插件追加 API 上限，只能保留在压缩包中。</summary>
    public bool Restorable { get; init; }

    public string Sha256 { get; init; } = "";
}
