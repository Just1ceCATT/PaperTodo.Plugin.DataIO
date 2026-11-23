namespace PaperTodo.Plugin.DataIo;

/// <summary>正文“最近操作”列表中的一行；只承载展示文本，不持有业务状态。</summary>
public sealed class HistoryRowItem
{
    public bool IsExport { get; init; }
    public string Glyph { get; init; } = "";
    public string Title { get; init; } = "";

    /// <summary>文件名 · 格式 · 数量；文件名在前，长文本截断时优先保留。</summary>
    public string Detail { get; init; } = "";
    public string TimeText { get; init; } = "";
    public string ToolTip { get; init; } = "";
    public bool ShowTopSeparator { get; init; }

    internal static HistoryRowItem From(
        DataIoHistoryEntry entry,
        DateTimeOffset now,
        bool showTopSeparator)
    {
        var detail = DataIoHistoryText.OperationDetail(entry);
        return new HistoryRowItem
        {
            IsExport = entry.IsExport,
            Glyph = entry.IsExport ? DataIoGlyphs.Export : DataIoGlyphs.Import,
            Title = DataIoHistoryText.OperationTitle(entry),
            Detail = detail,
            TimeText = DataIoHistoryText.FormatTime(entry.At, now),
            ToolTip = string.IsNullOrWhiteSpace(entry.FilePath)
                ? detail
                : entry.FilePath,
            ShowTopSeparator = showTopSeparator
        };
    }
}
