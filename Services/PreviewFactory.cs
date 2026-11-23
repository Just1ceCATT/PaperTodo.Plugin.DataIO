using PaperTodo.Plugin;

namespace PaperTodo.Plugin.DataIo;

/// <summary>
/// 构建预览快照：导出模式通过宿主只读读取，导入模式直接使用文件内容。
/// 与会话解耦，避免会话类继续膨胀，也便于单独测试文本格式化。
/// </summary>
internal static class PreviewFactory
{
    private const string MarkdownProviderId = "builtin.markdown";

    public static async Task<PreviewContent?> BuildExportAsync(
        HostGateway gateway,
        PaperSnapshot? paper,
        TodoSnapshot[]? storedTodos)
    {
        if (paper is null)
        {
            return null;
        }

        var isNote = string.Equals(paper.Type, "note", StringComparison.Ordinal);
        var title = string.IsNullOrWhiteSpace(paper.Title) ? "（无标题）" : paper.Title;

        if (!isNote)
        {
            var items = (storedTodos ?? [])
                .Select(todo => new PreviewTodoItem(todo.Text, todo.Done, DescribeHostTodo(todo)))
                .ToList();
            var done = items.Count(item => item.Done);

            return new PreviewContent
            {
                Key = paper.Id,
                Title = title,
                TypeLabel = "待办",
                IsTodo = true,
                Meta = items.Count == 0
                    ? "暂无待办"
                    : $"{items.Count} 项待办 · {done} 项已完成",
                Todos = items
            };
        }

        NoteSnapshot? note = null;
        if (gateway.Can(PaperTodoPermissionNames.NotesRead))
        {
            try
            {
                note = await gateway.OnUiAsync(
                    () => gateway.Context.Workspace.GetNote(paper.Id));
            }
            catch (Exception)
            {
                note = null;
            }
        }

        var body = note?.Content ?? "";
        var providerSupported = string.Equals(
            paper.BodyProviderId,
            MarkdownProviderId,
            StringComparison.Ordinal);
        var available = providerSupported && (note?.ContentAvailable ?? false);

        string? warning = null;
        if (!providerSupported)
        {
            warning = "正文由其他插件提供，无法通过逻辑 API 读取。";
        }
        else if (!available)
        {
            warning = "无法读取这张笔记的正文。";
        }

        return new PreviewContent
        {
            Key = paper.Id,
            Title = title,
            TypeLabel = "笔记",
            Meta = body.Length == 0 ? "空白笔记" : $"{body.Length} 字",
            Warning = warning,
            ContentAvailable = available,
            Body = available ? body : ""
        };
    }

    public static PreviewContent? BuildImport(ImportPlan? plan, string key)
    {
        if (plan is null ||
            !int.TryParse(key, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var index) ||
            index < 0 ||
            index >= plan.Papers.Count)
        {
            return null;
        }

        var source = plan.Papers[index].Source;
        var isNote = string.Equals(source.Type, "note", StringComparison.OrdinalIgnoreCase);
        var title = string.IsNullOrWhiteSpace(source.Title) ? "（无标题）" : source.Title;

        if (!isNote)
        {
            var items = (source.Todos ?? [])
                .OrderBy(todo => todo.Order)
                .Select(todo => new PreviewTodoItem(todo.Text, todo.Done, DescribeImportTodo(todo)))
                .ToList();
            var done = items.Count(item => item.Done);

            return new PreviewContent
            {
                Key = key,
                Title = title,
                TypeLabel = "待办",
                IsTodo = true,
                Meta = items.Count == 0
                    ? "暂无待办"
                    : $"{items.Count} 项待办 · {done} 项已完成",
                Todos = items
            };
        }

        var body = source.Content ?? "";
        var available = source.ContentAvailable != false;

        string? warning = null;
        if (!available)
        {
            warning = "文件未包含笔记正文，导入后将是空白笔记。";
        }
        else if (body.Length == 0)
        {
            warning = "这张笔记在文件中是空白的。";
        }

        return new PreviewContent
        {
            Key = key,
            Title = title,
            TypeLabel = "笔记",
            Meta = body.Length == 0 ? "空白笔记" : $"{body.Length} 字",
            Warning = warning,
            ContentAvailable = available,
            Body = available ? body : ""
        };
    }

    private static string DescribeHostTodo(TodoSnapshot todo)
    {
        var parts = new List<string>(3);
        if (todo.ReminderAt is { } reminder)
        {
            parts.Add($"提醒 {reminder.ToLocalTime():M-d HH:mm}");
        }

        if (!string.IsNullOrWhiteSpace(todo.LinkedPaperId))
        {
            parts.Add("关联纸片");
        }

        if (!string.IsNullOrWhiteSpace(todo.LinkedPath))
        {
            parts.Add(todo.LinkedPathIsDirectory == true ? "关联文件夹" : "关联文件");
        }

        return string.Join(" · ", parts);
    }

    private static string DescribeImportTodo(ExportTodo todo)
    {
        var parts = new List<string>(3);
        if (todo.ReminderAt is { } reminder)
        {
            parts.Add($"提醒 {reminder.ToLocalTime():M-d HH:mm}");
        }

        if (!string.IsNullOrWhiteSpace(todo.LinkedSourcePaperId))
        {
            parts.Add("文件内关联纸片");
        }

        if (!string.IsNullOrWhiteSpace(todo.LinkedPath))
        {
            parts.Add(todo.LinkedPathIsDirectory == true ? "关联文件夹" : "关联文件");
        }

        return string.Join(" · ", parts);
    }
}
