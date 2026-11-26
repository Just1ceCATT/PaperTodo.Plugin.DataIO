namespace PaperTodo.Plugin.DataIo;

/// <summary>
/// 预览窗口与悬停卡片共用的只读快照，不引用 PaperTodo 对象。
/// 导出模式由 PaperTodo API 即时读取，导入模式直接来自文件内容。
/// </summary>
internal sealed class PreviewContent
{
    /// <summary>与列表行一致：导出时为纸片 ID，导入时为计划内序号。</summary>
    public required string Key { get; init; }
    public required string Title { get; init; }
    public required string TypeLabel { get; init; }

    /// <summary>true 表示待办纸片；用于区分“空的待办”与“空的笔记”的占位文案。</summary>
    public bool IsTodo { get; init; }

    public string Meta { get; init; } = "";

    /// <summary>正文缺失、由第三方插件提供等提示；为空表示无需提示。</summary>
    public string? Warning { get; init; }

    /// <summary>false 表示正文确实不可读取，预览区应显示占位而不是“空白笔记”。</summary>
    public bool ContentAvailable { get; init; } = true;
    public string Body { get; init; } = "";
    public IReadOnlyList<PreviewTodoItem> Todos { get; init; } = [];

    /// <summary>
    /// 正文图片引用的编码数据，键与 Markdown 中的地址一致（i:图片ID 或 images/ 相对路径）。
    /// 只保存原始字节，实际解码交给预览渲染，避免缓存里存放放大后的位图。
    /// </summary>
    public IReadOnlyDictionary<string, PreviewImage> Images { get; init; } =
        new Dictionary<string, PreviewImage>(StringComparer.Ordinal);
}

/// <summary>预览窗口可直接使用的图片字节；内容来自 PaperTodo 图片库或导入压缩包。</summary>
internal sealed record PreviewImage(string Mime, byte[] Bytes);

/// <summary>预览中的一条待办，Meta 汇总提醒与链接等附加信息。</summary>
internal sealed record PreviewTodoItem(string Text, bool Done, string Meta)
{
    public bool HasMeta => Meta.Length > 0;
}
