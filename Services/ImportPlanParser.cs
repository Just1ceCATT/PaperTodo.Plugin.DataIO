using System.Text.Json;

namespace PaperTodo.Plugin.DataIo;

/// <summary>
/// 读取并校验导入文件。整个过程只处理内存数据，不接触宿主 API，可安全地在后台线程运行。
/// </summary>
internal static class ImportPlanParser
{
    public const int MaxPapers = 200;
    public const int MaxNoteChars = 100_000;
    public const int MaxTodoTextChars = 5_000;
    public const int MaxTodosPerPaper = 200;

    public static ImportPlan ParseFile(string path) => Parse(ArchiveService.ReadPayload(path));

    public static ImportPlan Parse(string json)
    {
        var plan = new ImportPlan();

        ExportDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<ExportDocument>(json, ExportJson.Options);
        }
        catch (JsonException ex)
        {
            plan.FatalError = $"不是有效的 JSON 文件：{ex.Message}";
            return plan;
        }

        if (document is null)
        {
            plan.FatalError = "文件内容为空。";
            return plan;
        }

        // 显式 null 的 papers 视为空列表，避免后续 NullReferenceException。
        document.Papers ??= [];

        if (!string.Equals(document.Format, ExportDocument.FormatName, StringComparison.OrdinalIgnoreCase))
        {
            plan.FatalError = "不是 PaperTodo 数据导入导出插件生成的文件（format 不匹配）。";
            return plan;
        }

        if (document.SchemaVersion != ExportDocument.CurrentSchemaVersion)
        {
            plan.FatalError = document.SchemaVersion > ExportDocument.CurrentSchemaVersion
                ? $"文件由更新版本的插件导出（schemaVersion={document.SchemaVersion}），请先升级插件。"
                : $"不支持的文件版本（schemaVersion={document.SchemaVersion}）。";
            return plan;
        }

        if (document.Papers.Count == 0)
        {
            plan.FatalError = "文件中没有任何纸片。";
            return plan;
        }

        if (document.Papers.Count > MaxPapers)
        {
            plan.FatalError = $"文件包含 {document.Papers.Count} 张纸片，超过宿主单工作区 {MaxPapers} 张上限。";
            return plan;
        }

        // 先确认 sourceId 唯一，重复会让同文件内的链接映射产生歧义。
        var sourceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var paper in document.Papers)
        {
            if (string.IsNullOrWhiteSpace(paper.SourceId))
            {
                continue;
            }

            if (!sourceIds.Add(paper.SourceId))
            {
                plan.FatalError = $"文件包含重复的 sourceId：{paper.SourceId}。";
                return plan;
            }
        }

        var now = DateTimeOffset.Now;

        foreach (var source in document.Papers)
        {
            var type = (source.Type ?? "").Trim().ToLowerInvariant();

            if (type is not ("todo" or "note"))
            {
                plan.Warnings.Add($"跳过未知类型“{source.Type}”的纸片“{source.Title}”。");
                continue;
            }

            if (type == "note" && source.Todos is { Count: > 0 })
            {
                plan.Warnings.Add($"跳过笔记“{source.Title}”：笔记不能包含待办。");
                continue;
            }

            if (type == "todo" && !string.IsNullOrEmpty(source.Content))
            {
                plan.Warnings.Add($"跳过待办纸片“{source.Title}”：待办纸片不能包含笔记正文。");
                continue;
            }

            if (type == "note" && (source.Content?.Length ?? 0) > MaxNoteChars)
            {
                plan.Warnings.Add($"跳过笔记“{source.Title}”：正文超过宿主 {MaxNoteChars} 字符上限。");
                continue;
            }

            if (type == "todo")
            {
                source.Todos ??= [];
                if (source.Todos.Count > MaxTodosPerPaper)
                {
                    plan.Warnings.Add($"跳过待办纸片“{source.Title}”：待办数量 {source.Todos.Count} 超过 {MaxTodosPerPaper} 条上限。");
                    continue;
                }

                if (source.Todos.Any(t => (t.Text?.Length ?? 0) > MaxTodoTextChars))
                {
                    plan.Warnings.Add($"跳过待办纸片“{source.Title}”：存在超过 {MaxTodoTextChars} 字符的待办。");
                    continue;
                }

                source.Todos.RemoveAll(t => string.IsNullOrWhiteSpace(t.Text));
                plan.TodoCount += source.Todos.Count;

                foreach (var todo in source.Todos)
                {
                    if (!string.IsNullOrWhiteSpace(todo.LinkedSourcePaperId) &&
                        !sourceIds.Contains(todo.LinkedSourcePaperId))
                    {
                        plan.UnresolvedLinks++;
                    }

                    if (!string.IsNullOrWhiteSpace(todo.LinkedPath))
                    {
                        plan.PathLinkCount++;
                    }

                    if (!todo.Done && todo.ReminderAt is { } reminder && reminder <= now)
                    {
                        plan.DroppedReminders++;
                    }
                }
            }

            var normalizedTitle = TitleSanitizer.Normalize(source.Title);
            string? safeTitle = normalizedTitle;
            if (normalizedTitle.Length == 0)
            {
                safeTitle = null;
                plan.Warnings.Add($"纸片“{source.Title}”标题为空，导入时将使用宿主默认标题。");
            }
            else if (TitleSanitizer.DisplayWidth(normalizedTitle) > TitleSanitizer.PreTruncateDisplayWidth)
            {
                safeTitle = TitleSanitizer.TruncateByDisplayWidth(normalizedTitle, TitleSanitizer.PreTruncateDisplayWidth);
                plan.TruncatedTitles++;
                plan.Warnings.Add($"纸片标题“{normalizedTitle}”过长，将截断为“{safeTitle}”。");
            }

            if (type == "note")
            {
                var imageReferences = ImageReferenceScanner.Count(source.Content);
                if (imageReferences > 0)
                {
                    plan.NotesWithImages++;
                    plan.ImageReferenceCount += imageReferences;
                }
            }

            plan.Papers.Add(new ImportPaper
            {
                Source = source,
                SafeTitle = safeTitle
            });
        }

        if (plan.Papers.Count == 0)
        {
            plan.FatalError = "文件中没有可导入的纸片。";
        }
        else if (plan.Papers.Count + 0 > MaxPapers)
        {
            plan.FatalError = $"可导入纸片超过 {MaxPapers} 张上限。";
        }

        return plan;
    }
}
