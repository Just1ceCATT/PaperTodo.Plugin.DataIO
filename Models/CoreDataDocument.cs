using System.Text.Json;

namespace PaperTodo.Plugin.DataIo;

/// <summary>
/// PaperTodo 主数据文件（data.json）的最小读取模型。
/// 只读取导入需要的字段，其余窗口/外观数据一律忽略。
/// </summary>
internal sealed class CoreDataDocument
{
    public List<CoreDataPaper>? Papers { get; set; }
}

internal sealed class CoreDataPaper
{
    /// <summary>核心纸片 ID；作为导入身份映射的来源 ID。</summary>
    public string Id { get; set; } = "";

    /// <summary>兼容同时带 sourceId 的文件形状。</summary>
    public string? SourceId { get; set; }

    public string Type { get; set; } = "";
    public string Title { get; set; } = "";
    public bool IsVisible { get; set; } = true;

    /// <summary>正文提供者；只有内置 Markdown 的正文能通过其它方式读出。</summary>
    public string? BodyProviderId { get; set; }

    /// <summary>
    /// 用 JsonElement 保留“字段缺失/显式 null/空串”的区别：
    /// 只有确定为字符串时才算正文可读，缺失时不能当成空正文去覆盖本地笔记。
    /// </summary>
    public JsonElement Content { get; set; }

    public List<CoreDataItem>? Items { get; set; }
}

internal sealed class CoreDataItem
{
    public string Text { get; set; } = "";
    public bool Done { get; set; }
    public int Order { get; set; }
    public DateTimeOffset? ReminderAt { get; set; }

    /// <summary>核心数据里的纸片关联字段名是 linkedNoteId。</summary>
    public string? LinkedNoteId { get; set; }

    /// <summary>兼容部分版本/手工文件使用的 linkedPaperId。</summary>
    public string? LinkedPaperId { get; set; }

    public string? LinkedPath { get; set; }
    public bool? LinkedPathIsDirectory { get; set; }
}
