using System.IO;
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

    public static ImportPlan ParseFile(string path)
    {
        var extension = Path.GetExtension(path);
        if (string.Equals(extension, ".md", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".markdown", StringComparison.OrdinalIgnoreCase))
        {
            return ParseMarkdown(JsonFiles.ReadAllText(path), path);
        }

        return Parse(DataFileService.ReadPayload(path));
    }

    private static ImportPlan ParseMarkdown(string markdown, string path)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return new ImportPlan { FatalError = "Markdown 文件为空。" };
        }

        // Markdown 解析结果直接走同一套校验，不再先序列化成 JSON 再解析。
        var plan = ParseDocument(MarkdownImportParser.Parse(markdown, path));
        if (plan.FatalError == null)
        {
            plan.Warnings.Add("Markdown 文件不包含提醒、关联纸片、可见性等 PaperTodo 专有数据。");
        }

        return plan;
    }

    public static ImportPlan Parse(string json)
    {
        JsonDocument document;
        try
        {
            // 与宿主读取 data.json 的容错保持一致：允许注释和尾随逗号。
            document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
        }
        catch (JsonException)
        {
            return InvalidJsonPlan();
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Null)
            {
                return new ImportPlan { FatalError = "文件内容为空。" };
            }

            if (root.ValueKind != JsonValueKind.Object)
            {
                return UnsupportedPlan();
            }

            if (TryGetProperty(root, "format", out var format))
            {
                // 只有明确声明导出格式的文件才走导出解析；核心 data.json 没有 format 字段。
                if (format.ValueKind != JsonValueKind.String ||
                    !string.Equals(
                        format.GetString(),
                        ExportDocument.FormatName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return UnsupportedPlan();
                }

                return ParseExport(json, root);
            }

            // PaperTodo 主数据文件没有格式标记；还要确认纸片形状确实是核心数据，
            // 避免把“format 被删掉/改坏”的导出文件按错误字段静默读取。
            if (TryGetProperty(root, "papers", out var papers) &&
                papers.ValueKind == JsonValueKind.Array &&
                LooksLikeCoreData(root, papers))
            {
                return ParseCoreData(json);
            }

            return UnsupportedPlan();
        }
    }

    /// <summary>
    /// 解析插件自己的导出文件。schemaVersion 必须在原始 JSON 中确认：
    /// ExportDocument 的属性初始化器会让缺失字段也带上默认值，不能靠反序列化结果判断。
    /// </summary>
    private static ImportPlan ParseExport(string json, JsonElement root)
    {
        if (!TryGetProperty(root, "schemaVersion", out var schema) ||
            schema.ValueKind != JsonValueKind.Number ||
            !schema.TryGetInt32(out var version))
        {
            return new ImportPlan
            {
                FatalError = "文件缺少 schemaVersion，无法确认导出格式版本；请重新用 DataIO 导出。"
            };
        }

        ExportDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<ExportDocument>(json, ExportJson.Options);
        }
        catch (JsonException)
        {
            return InvalidJsonPlan();
        }

        if (document is null)
        {
            return new ImportPlan { FatalError = "文件内容为空。" };
        }

        if (version != ExportDocument.CurrentSchemaVersion)
        {
            return new ImportPlan
            {
                FatalError = version > ExportDocument.CurrentSchemaVersion
                    ? $"文件由更新版本的插件导出（schemaVersion={version}），请先升级插件。"
                    : $"不支持的文件版本（schemaVersion={version}）。"
            };
        }

        return ParseDocument(document);
    }

    /// <summary>
    /// 把 PaperTodo 主数据文件（data.json）转换为导出文档：
    /// id → sourceId（保证再次导入能命中映射），items → 待办，isVisible → 可见性。
    /// 第三方插件正文无法读取，直接跳过并提示，避免导入成空纸片。
    /// </summary>
    private static ImportPlan ParseCoreData(string json)
    {
        CoreDataDocument? core;
        try
        {
            core = JsonSerializer.Deserialize<CoreDataDocument>(json, ExportJson.Options);
        }
        catch (JsonException)
        {
            return InvalidJsonPlan();
        }

        if (core?.Papers is null)
        {
            return UnsupportedPlan();
        }

        var document = new ExportDocument();
        var skippedPluginBodies = 0;
        var missingSourceIds = 0;

        foreach (var paper in core.Papers.OfType<CoreDataPaper>())
        {
            var type = (paper.Type ?? "").Trim().ToLowerInvariant();
            var markdownBody = IsMarkdownBody(paper.BodyProviderId);
            if (type == "note" && !markdownBody)
            {
                skippedPluginBodies++;
                continue;
            }

            var sourceId = string.IsNullOrWhiteSpace(paper.SourceId)
                ? (paper.Id ?? "").Trim()
                : paper.SourceId.Trim();
            if (sourceId.Length == 0)
            {
                missingSourceIds++;
            }

            var export = new ExportPaper
            {
                SourceId = sourceId,
                Type = paper.Type ?? "",
                Title = paper.Title ?? "",
                Show = paper.IsVisible
            };

            if (type == "note")
            {
                // 只有明确的字符串才算正文可读；缺失/null 时保留本地正文，禁止被空串覆盖。
                var hasContent = markdownBody && paper.Content.ValueKind == JsonValueKind.String;
                export.ContentAvailable = hasContent;
                export.Content = hasContent ? paper.Content.GetString() ?? "" : "";
            }
            else if (type == "todo")
            {
                export.Todos = (paper.Items ?? [])
                    .OfType<CoreDataItem>()
                    .Select(item => new ExportTodo
                    {
                        Text = item.Text ?? "",
                        Done = item.Done,
                        Order = item.Order,
                        ReminderAt = item.ReminderAt,
                        LinkedSourcePaperId = string.IsNullOrWhiteSpace(item.LinkedNoteId)
                            ? item.LinkedPaperId
                            : item.LinkedNoteId,
                        LinkedPath = item.LinkedPath,
                        LinkedPathIsDirectory = item.LinkedPathIsDirectory
                    })
                    .ToList();
            }

            document.Papers.Add(export);
        }

        var plan = ParseDocument(document);
        if (plan.FatalError is not null)
        {
            if (skippedPluginBodies > 0)
            {
                plan.FatalError +=
                    $"\n已跳过 {skippedPluginBodies} 张由其他插件绘制正文的纸片，无法读取它们的正文。";
            }

            return plan;
        }

        plan.Warnings.Add(
            "检测到 PaperTodo 主数据文件（data.json）：将按纸片导入；" +
            "窗口位置、折叠/置顶状态和图片本体不会保留。");
        if (skippedPluginBodies > 0)
        {
            plan.Warnings.Add(
                $"已跳过 {skippedPluginBodies} 张由其他插件绘制正文的纸片，无法读取它们的正文。");
        }

        if (missingSourceIds > 0)
        {
            plan.Warnings.Add($"{missingSourceIds} 张纸片缺少来源 ID，再次导入时会新建副本。");
        }

        return plan;
    }

    private static bool IsMarkdownBody(string? providerId) =>
        string.IsNullOrWhiteSpace(providerId) ||
        string.Equals(providerId.Trim(), PaperProviders.Markdown, StringComparison.Ordinal);

    /// <summary>
    /// 核心 data.json 的纸片使用 isVisible/items，而插件导出使用 sourceId/todos/show。
    /// 只有形状确实是核心数据时才走核心解析，避免误读被删掉 format 的导出文件。
    /// </summary>
    private static bool LooksLikeCoreData(JsonElement root, JsonElement papers)
    {
        foreach (var paper in papers.EnumerateArray())
        {
            if (paper.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (TryGetProperty(paper, "sourceId", out _) ||
                TryGetProperty(paper, "todos", out _) ||
                TryGetProperty(paper, "show", out _))
            {
                return false;
            }

            // 核心纸片使用 id/bodyProviderId/isVisible/items；导出纸片使用 sourceId/todos/show。
            if (TryGetProperty(paper, "id", out _) ||
                TryGetProperty(paper, "bodyProviderId", out _) ||
                TryGetProperty(paper, "isVisible", out _) ||
                TryGetProperty(paper, "items", out _))
            {
                return true;
            }
        }

        // 空数组或字段不完整时，用核心数据典型的顶层字段兜底。
        return TryGetProperty(root, "uiLanguage", out _) ||
               TryGetProperty(root, "theme", out _) ||
               TryGetProperty(root, "colorScheme", out _) ||
               TryGetProperty(root, "markdownRenderMode", out _) ||
               TryGetProperty(root, "todoVisualSize", out _);
    }

    /// <summary>属性名不区分大小写，兼容被其他工具改写为 PascalCase 的文件。</summary>
    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static ImportPlan InvalidJsonPlan() => new()
    {
        // 不向用户暴露底层解析器的英文错误；直接说明可能原因与处理方式。
        FatalError =
            "无法读取该文件：内容不是有效的 JSON。\n"
            + "请选择由本插件导出的 .json、PaperTodo 主数据文件 data.json；" +
            "如果是 Markdown 笔记，请确认文件扩展名为 .md 后重新导入。"
    };

    private static ImportPlan UnsupportedPlan() => new()
    {
        FatalError =
            "该文件不是可导入的数据文件。\n"
            + "请选择由本插件导出的 JSON、PaperTodo 主数据文件 data.json，或 Markdown 文件。"
    };

    /// <summary>校验内存中的导出文档并生成导入计划（JSON 与 Markdown 共用）。</summary>
    private static ImportPlan ParseDocument(ExportDocument document)
    {
        var plan = new ImportPlan();

        // 显式 null 或数组内混入 null 都要过滤，避免后续访问元素时抛空引用。
        document.Papers = document.Papers?.OfType<ExportPaper>().ToList() ?? [];

        if (!string.Equals(document.Format, ExportDocument.FormatName, StringComparison.OrdinalIgnoreCase))
        {
            plan.FatalError =
                "该文件不是本插件导出的数据。\n"
                + "请确认选择的是通过“导出数据”生成的 JSON 文件，且文件内容未被其他程序改写。";
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
            plan.FatalError = $"文件包含 {document.Papers.Count} 张纸片，超过单个工作区 {MaxPapers} 张上限。";
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

            // 只保留结构完整的待办条目，并把显式 null 文本归一为空串。
            source.Todos = source.Todos?.OfType<ExportTodo>().ToList();
            foreach (var todo in source.Todos ?? [])
            {
                todo.Text ??= "";
            }

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
                plan.Warnings.Add($"跳过笔记“{source.Title}”：正文超过 {MaxNoteChars} 字符上限。");
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
                plan.Warnings.Add($"纸片“{source.Title}”标题为空，导入时将使用默认标题。");
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

                // 旧版 Markdown 导出会把图片引用替换为缺失标记；识别出来以便在更新时拦截。
                if (ImageReferenceScanner.ContainsMissingLabel(
                        source.Content,
                        MarkdownExportService.MissingImageLabel))
                {
                    plan.LossyImageNotes++;
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

        if (plan.LossyImageNotes > 0)
        {
            plan.Warnings.Add(
                $"{plan.LossyImageNotes} 张笔记的正文含“图片缺失”标记（旧版 Markdown 导出的结果）。" +
                "更新这些纸片会覆盖本地图片引用，已禁止更新，建议改用 JSON 文件或选择新建。");
        }

        return plan;
    }
}
