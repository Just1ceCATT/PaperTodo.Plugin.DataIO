using System.Globalization;
using System.IO;
using System.Text.Json.Serialization;

namespace PaperTodo.Plugin.DataIo;

/// <summary>历史记录类型。</summary>
internal static class DataIoHistoryKinds
{
    public const string Export = "export";
    public const string Import = "import";
}

/// <summary>
/// 一条导入或导出记录，保存在插件状态中，供贴边胶囊的悬停面板展示。
/// 只记录文件路径、数量与结果，不保存纸片正文。
/// </summary>
internal sealed class DataIoHistoryEntry
{
    public string Kind { get; set; } = "";

    /// <summary>发生时间，ISO 8601 往返格式；无效时间的历史条目会被丢弃。</summary>
    public string At { get; set; } = "";

    public string FilePath { get; set; } = "";

    /// <summary>导出或导入文件格式："json" / "markdown"；无法识别时为空。</summary>
    public string Format { get; set; } = "";

    /// <summary>导出纸片张数；导入时不用。</summary>
    public int Papers { get; set; }

    public int Created { get; set; }
    public int Updated { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }

    /// <summary>警告条数；保留用于排查问题，不在悬停面板显示标记。</summary>
    public int Warnings { get; set; }

    /// <summary>导入被中断（会话关闭或手动取消），已完成部分不会回滚。</summary>
    public bool Cancelled { get; set; }

    /// <summary>导入因容量、权限等严重错误提前停止。</summary>
    public bool StoppedEarly { get; set; }

    [JsonIgnore]
    public bool IsExport => string.Equals(Kind, DataIoHistoryKinds.Export, StringComparison.Ordinal);

    [JsonIgnore]
    public bool IsImport => string.Equals(Kind, DataIoHistoryKinds.Import, StringComparison.Ordinal);
}

internal enum DataIoHistoryBadgeTone
{
    Accent,
    Neutral,
    Warning
}

/// <summary>数量徽标：文本与色调由展示层决定具体画刷。</summary>
internal readonly record struct DataIoHistoryBadge(string Text, DataIoHistoryBadgeTone Tone);

/// <summary>
/// 历史记录的展示文案。独立成静态方法便于测试，也让 MiniView 只负责排版。
/// </summary>
internal static class DataIoHistoryText
{
    /// <summary>悬停面板短标题；数量交给徽标展示，避免标题过长。</summary>
    public static string MiniTitle(DataIoHistoryEntry entry)
    {
        if (entry.IsExport)
        {
            return "导出";
        }

        return entry.Cancelled
            ? "导入中断"
            : entry.StoppedEarly
                ? "导入未完成"
                : "导入";
    }

    /// <summary>
    /// 数量徽标：导出显示纸片数；导入把新增+更新合并为“成功”，
    /// 再分别展示跳过与失败，读起来比连续的“·”分隔更清晰。
    /// </summary>
    public static List<DataIoHistoryBadge> Badges(DataIoHistoryEntry entry)
    {
        var badges = new List<DataIoHistoryBadge>(3);
        if (entry.IsExport)
        {
            badges.Add(new($"{entry.Papers} 张纸片", DataIoHistoryBadgeTone.Accent));
            return badges;
        }

        var succeeded = entry.Created + entry.Updated;
        if (succeeded > 0)
        {
            badges.Add(new($"成功 {succeeded}", DataIoHistoryBadgeTone.Accent));
        }

        if (entry.Skipped > 0)
        {
            badges.Add(new($"跳过 {entry.Skipped}", DataIoHistoryBadgeTone.Neutral));
        }

        if (entry.Failed > 0)
        {
            badges.Add(new($"失败 {entry.Failed}", DataIoHistoryBadgeTone.Warning));
        }

        if (badges.Count == 0)
        {
            badges.Add(new("无变化", DataIoHistoryBadgeTone.Neutral));
        }

        return badges;
    }

    /// <summary>用“ / ”连接徽标文本，供正文的单行明细使用。</summary>
    public static string BadgeSummary(DataIoHistoryEntry entry) =>
        string.Join(" / ", Badges(entry).Select(badge => badge.Text));

    /// <summary>悬停面板副标题：格式 · 文件名；长文件名保留头部与扩展名。</summary>
    public static string MiniSubtitle(DataIoHistoryEntry entry)
    {
        var name = CompactFileName(entry.FilePath, 24);
        var format = FormatLabel(entry);
        return string.IsNullOrEmpty(format) ? name : $"{format} · {name}";
    }

    /// <summary>正文“最近操作”的标题。</summary>
    public static string OperationTitle(DataIoHistoryEntry entry)
    {
        if (entry.IsExport)
        {
            return "导出数据";
        }

        return entry.Cancelled
            ? "导入已中断"
            : entry.StoppedEarly
                ? "导入未完成"
                : "导入数据";
    }

    /// <summary>
    /// 正文“最近操作”的明细：文件名放在最前，确保长文本截断时仍能看到文件；
    /// 随后是格式与数量（“成功 / 跳过 / 失败”）。
    /// </summary>
    public static string OperationDetail(DataIoHistoryEntry entry)
    {
        var parts = new List<string>(4) { CompactFileName(entry.FilePath, 40) };

        var format = FormatLabel(entry);
        if (!string.IsNullOrEmpty(format))
        {
            parts.Add(format);
        }

        parts.Add(BadgeSummary(entry));
        return string.Join(" · ", parts);
    }

    public static string FormatLabel(DataIoHistoryEntry entry)
    {
        if (string.Equals(entry.Format, "markdown", StringComparison.OrdinalIgnoreCase))
        {
            return "Markdown";
        }

        if (string.Equals(entry.Format, "json", StringComparison.OrdinalIgnoreCase))
        {
            return "JSON";
        }

        return Path.GetExtension(entry.FilePath).ToLowerInvariant() switch
        {
            ".md" or ".markdown" => "Markdown",
            ".json" => "JSON",
            _ => ""
        };
    }

    public static string FileName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "未记录文件";
        }

        try
        {
            var name = Path.GetFileName(path);
            return string.IsNullOrWhiteSpace(name) ? path : name;
        }
        catch (ArgumentException)
        {
            return path;
        }
    }

    /// <summary>
    /// 长文件名中部省略：保留开头与扩展名，避免尾部被截断后看不出文件类型。
    /// maxDisplayWidth 使用与标题一致的全角/半角显示宽度单位。
    /// </summary>
    public static string CompactFileName(string? path, int maxDisplayWidth)
    {
        var name = FileName(path);
        if (maxDisplayWidth <= 0 ||
            TitleSanitizer.DisplayWidth(name) <= maxDisplayWidth)
        {
            return name;
        }

        var extension = Path.GetExtension(name);
        var stem = Path.GetFileNameWithoutExtension(name);
        if (string.IsNullOrEmpty(stem))
        {
            return TitleSanitizer.TruncateByDisplayWidth(name, maxDisplayWidth);
        }

        const string ellipsis = "…";
        var budget = maxDisplayWidth -
            TitleSanitizer.DisplayWidth(extension) -
            TitleSanitizer.DisplayWidth(ellipsis);
        if (budget <= 0)
        {
            return TitleSanitizer.TruncateByDisplayWidth(name, maxDisplayWidth);
        }

        return TitleSanitizer.TruncateByDisplayWidth(stem, budget) + ellipsis + extension;
    }

    public static string FormatTime(string? at, DateTimeOffset now) =>
        DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? FormatRelativeTime(parsed, now)
            : "";

    /// <summary>
    /// 中文相对时间：刚刚 / N 分钟前 / 今天 HH:mm / 昨天 HH:mm / MM-dd HH:mm / yyyy-MM-dd HH:mm。
    /// 统一换算到 now 的时区再比较日期，避免跨时区或夏令时影响。
    /// </summary>
    public static string FormatRelativeTime(DateTimeOffset at, DateTimeOffset now)
    {
        var localAt = at.ToOffset(now.Offset);
        var delta = now - at;
        if (delta < TimeSpan.FromMinutes(1))
        {
            return "刚刚";
        }

        if (delta < TimeSpan.FromHours(1))
        {
            return $"{(int)delta.TotalMinutes} 分钟前";
        }

        if (localAt.Date == now.Date)
        {
            return $"今天 {localAt.ToString("HH:mm", CultureInfo.InvariantCulture)}";
        }

        if (localAt.Date == now.Date.AddDays(-1))
        {
            return $"昨天 {localAt.ToString("HH:mm", CultureInfo.InvariantCulture)}";
        }

        return localAt.Year == now.Year
            ? localAt.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture)
            : localAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }
}
