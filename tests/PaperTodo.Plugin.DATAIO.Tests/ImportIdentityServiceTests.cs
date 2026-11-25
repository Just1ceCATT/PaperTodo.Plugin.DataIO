using System.Globalization;
using PaperTodo.Plugin;
using Xunit;

namespace PaperTodo.Plugin.DataIo.Tests;

/// <summary>身份映射的历史副本回退：删除“新增”出来的副本后仍能匹配原副本。</summary>
public sealed class ImportIdentityServiceTests
{
    [Fact]
    public void Deleting_new_copy_falls_back_to_previous_copy()
    {
        var state = new DataIoState();
        state.ImportLinks["s"] = new ImportIdentityLink
        {
            LocalPaperId = "p2",
            Type = "todo",
            LocalFingerprint = "stale",
            PreviousLocalPaperIds = ["p1"]
        };

        var service = new ImportIdentityService(null!, state);
        service.RemoveByLocalPaperId("p2");

        var link = Assert.Single(state.ImportLinks).Value;
        Assert.Equal("p1", link.LocalPaperId);
        Assert.Equal("", link.LocalFingerprint);
        Assert.Empty(link.PreviousLocalPaperIds);
        Assert.True(service.HasChanges);
    }

    [Fact]
    public void Deleting_historical_copy_only_removes_it_from_history()
    {
        var state = new DataIoState();
        state.ImportLinks["s"] = new ImportIdentityLink
        {
            LocalPaperId = "p3",
            Type = "todo",
            LocalFingerprint = "fp",
            PreviousLocalPaperIds = ["p1", "p2"]
        };

        new ImportIdentityService(null!, state).RemoveByLocalPaperId("p1");

        var link = state.ImportLinks["s"];
        Assert.Equal("p3", link.LocalPaperId);
        Assert.Equal("fp", link.LocalFingerprint);
        Assert.Equal(["p2"], link.PreviousLocalPaperIds);
    }

    [Fact]
    public void Merge_history_pushes_previous_target_to_front()
    {
        Assert.Equal(["p2", "p1"], ImportIdentityService.MergeHistory("p2", ["p1"], "p3"));
        Assert.Empty(ImportIdentityService.MergeHistory("p1", null, "p1"));
        Assert.Equal(["p0"], ImportIdentityService.MergeHistory("p1", ["p0"], "p1"));
    }

    [Fact]
    public void Fallback_skips_missing_and_wrong_type_candidates()
    {
        var link = new ImportIdentityLink
        {
            LocalPaperId = "p9",
            Type = "todo",
            PreviousLocalPaperIds = ["missing", "note-copy", "todo-copy"]
        };
        var papers = new Dictionary<string, PaperSnapshot>
        {
            ["note-copy"] = new("note-copy", "note", "笔记", true, false, false, "builtin.markdown"),
            ["todo-copy"] = new("todo-copy", "todo", "待办", true, false, false, "builtin.todo")
        };

        var fallback = ImportIdentityService.FindFallback(link, papers, "todo");

        Assert.NotNull(fallback);
        Assert.Equal("todo-copy", fallback!.Id);
        Assert.Equal("todo-copy", link.LocalPaperId);
        Assert.Empty(link.PreviousLocalPaperIds);
    }

    [Fact]
    public void NoOp_is_detected_for_mapped_papers_without_any_change()
    {
        static ImportPaper Paper(
            string type,
            string? target,
            bool hasChanges,
            bool hasMetaChanges = false) => new()
        {
            Source = new ExportPaper { Type = type },
            UpdateTargetId = target,
            UpdateHasChanges = hasChanges,
            UpdateHasMetaChanges = hasMetaChanges
        };

        Assert.True(DataIoSession.IsNoOpUpdate(Paper("note", "p1", hasChanges: false)));
        Assert.False(DataIoSession.IsNoOpUpdate(Paper("note", "p1", hasChanges: true)));
        Assert.False(DataIoSession.IsNoOpUpdate(Paper("note", null, hasChanges: false)));
        // 待办文本没有变化，也没有顺序/提醒/关联差异时同样按无操作处理。
        Assert.True(DataIoSession.IsNoOpUpdate(Paper("todo", "p1", hasChanges: false)));
        Assert.False(DataIoSession.IsNoOpUpdate(
            Paper("todo", "p1", hasChanges: false, hasMetaChanges: true)));
    }

    [Fact]
    public void Todo_meta_changes_detect_order_reminder_and_link_differences()
    {
        var settings = new DataIoSettings();
        var local = new[]
        {
            Todo("a", "A", 0),
            Todo("b", "B", 1)
        };

        // 顺序变化。
        Assert.True(ImportIdentityService.HasTodoMetaChanges(
            local,
            [TodoSource("B", order: 0), TodoSource("A", order: 1)],
            settings,
            supportsPaperLinks: true,
            supportsReminders: true));

        // 完全相同。
        Assert.False(ImportIdentityService.HasTodoMetaChanges(
            local,
            [TodoSource("A", order: 0), TodoSource("B", order: 1)],
            settings,
            supportsPaperLinks: true,
            supportsReminders: true));

        // 提醒变化。
        Assert.True(ImportIdentityService.HasTodoMetaChanges(
            local,
            [TodoSource("A", order: 0, reminder: DateTimeOffset.Now.AddHours(1)), TodoSource("B", order: 1)],
            settings,
            supportsPaperLinks: true,
            supportsReminders: true));

        // 关联纸片变化。
        Assert.True(ImportIdentityService.HasTodoMetaChanges(
            [Todo("a", "A", 0, link: "paper-1")],
            [TodoSource("A", order: 0)],
            settings,
            supportsPaperLinks: true,
            supportsReminders: true));

        // Markdown 不携带提醒/关联，不能据此判定存在差异。
        Assert.False(ImportIdentityService.HasTodoMetaChanges(
            [Todo("a", "A", 0, link: "paper-1", reminder: DateTimeOffset.Now.AddHours(1))],
            [TodoSource("A", order: 0)],
            settings,
            supportsPaperLinks: false,
            supportsReminders: false));
    }

    private static TodoSnapshot Todo(
        string id,
        string text,
        int order,
        string? link = null,
        DateTimeOffset? reminder = null) =>
        new("p", "t", id, text, false, order, link, null, reminder);

    private static ExportTodo TodoSource(
        string text,
        int order,
        DateTimeOffset? reminder = null,
        string? linkedSourceId = null) => new()
    {
        Text = text,
        Order = order,
        ReminderAt = reminder,
        LinkedSourcePaperId = linkedSourceId
    };

    [Fact]
    public void Fallback_can_require_markdown_provider_for_notes()
    {
        var link = new ImportIdentityLink
        {
            LocalPaperId = "missing",
            Type = "note",
            PreviousLocalPaperIds = ["todo-copy", "other-note", "markdown-note"]
        };
        var papers = new Dictionary<string, PaperSnapshot>
        {
            ["todo-copy"] = new("todo-copy", "todo", "待办", true, false, false, "builtin.todo"),
            ["other-note"] = new("other-note", "note", "第三方", true, false, false, "other.plugin"),
            ["markdown-note"] = new("markdown-note", "note", "笔记", true, false, false, "builtin.markdown")
        };

        var fallback = ImportIdentityService.FindFallback(
            link,
            papers,
            "note",
            requireMarkdownProvider: true);

        Assert.NotNull(fallback);
        Assert.Equal("markdown-note", fallback!.Id);
    }

    [Fact]
    public void Normalize_history_drops_current_duplicates_and_empties()
    {
        var links = new Dictionary<string, ImportIdentityLink>
        {
            ["s"] = new()
            {
                LocalPaperId = "p2",
                Type = "todo",
                PreviousLocalPaperIds = ["p2", "p1", "p1", "", "p3"]
            }
        };

        var normalized = DataIoState.NormalizeImportLinks(links);

        Assert.Equal(["p1", "p3"], normalized["s"].PreviousLocalPaperIds);
    }

    [Fact]
    public void Deleting_target_skips_history_candidates_that_no_longer_exist()
    {
        var state = new DataIoState();
        state.ImportLinks["s"] = new ImportIdentityLink
        {
            LocalPaperId = "p3",
            Type = "todo",
            LocalFingerprint = "stale",
            PreviousLocalPaperIds = ["p1-dead", "p2"]
        };
        var papers = new Dictionary<string, PaperSnapshot>
        {
            ["p2"] = new("p2", "todo", "待办", true, false, false, "builtin.todo")
        };

        var service = new ImportIdentityService(
            null!,
            state,
            id => papers.GetValueOrDefault(id));
        service.RemoveByLocalPaperId("p3");

        var link = state.ImportLinks["s"];
        Assert.Equal("p2", link.LocalPaperId);
        Assert.Equal("", link.LocalFingerprint);
        Assert.Empty(link.PreviousLocalPaperIds);
    }

    [Fact]
    public void Deleting_target_keeps_mapping_when_lookup_is_temporarily_unavailable()
    {
        var state = new DataIoState();
        state.ImportLinks["s"] = new ImportIdentityLink
        {
            LocalPaperId = "p3",
            Type = "todo",
            PreviousLocalPaperIds = ["p1"]
        };

        var service = new ImportIdentityService(
            null!,
            state,
            _ => throw new PaperTodoPluginException("data_reload_in_progress", "正在重载数据"));
        service.RemoveByLocalPaperId("p3");

        // 无法确认历史副本是否存在时不能删除映射，留给下次导入校验。
        var link = state.ImportLinks["s"];
        Assert.Equal("p3", link.LocalPaperId);
        Assert.Equal(["p1"], link.PreviousLocalPaperIds);
    }

    [Fact]
    public void Reconcile_falls_back_when_target_was_deleted_while_events_were_lost()
    {
        var state = new DataIoState();
        state.ImportLinks["s"] = new ImportIdentityLink
        {
            LocalPaperId = "p2",
            Type = "todo",
            LocalFingerprint = "stale",
            PreviousLocalPaperIds = ["p1"]
        };
        var papers = new Dictionary<string, PaperSnapshot>
        {
            ["p1"] = new("p1", "todo", "待办", true, false, false, "builtin.todo")
        };

        var service = new ImportIdentityService(null!, state);
        service.Reconcile(papers);

        var link = state.ImportLinks["s"];
        Assert.Equal("p1", link.LocalPaperId);
        Assert.Equal("", link.LocalFingerprint);
        Assert.Empty(link.PreviousLocalPaperIds);
        Assert.True(service.HasChanges);
    }

    [Fact]
    public void Reconcile_removes_links_without_any_existing_copy()
    {
        var state = new DataIoState();
        state.ImportLinks["s"] = new ImportIdentityLink
        {
            LocalPaperId = "p2",
            Type = "todo",
            PreviousLocalPaperIds = ["p1"]
        };

        var service = new ImportIdentityService(null!, state);
        service.Reconcile(new Dictionary<string, PaperSnapshot>());

        Assert.Empty(state.ImportLinks);
        Assert.True(service.HasChanges);
    }

    [Fact]
    public void Reconcile_prunes_deleted_history_entries_and_keeps_fingerprint()
    {
        var state = new DataIoState();
        state.ImportLinks["s"] = new ImportIdentityLink
        {
            LocalPaperId = "p3",
            Type = "todo",
            LocalFingerprint = "fp",
            PreviousLocalPaperIds = ["p1", "p2"]
        };
        var papers = new Dictionary<string, PaperSnapshot>
        {
            ["p3"] = new("p3", "todo", "待办", true, false, false, "builtin.todo"),
            ["p2"] = new("p2", "todo", "旧副本", true, false, false, "builtin.todo")
        };

        var service = new ImportIdentityService(null!, state);
        service.Reconcile(papers);

        var link = state.ImportLinks["s"];
        Assert.Equal("p3", link.LocalPaperId);
        Assert.Equal("fp", link.LocalFingerprint);
        Assert.Equal(["p2"], link.PreviousLocalPaperIds);
        Assert.True(service.HasChanges);
    }

    [Fact]
    public void Reconcile_keeps_state_untouched_when_everything_is_valid()
    {
        var state = new DataIoState();
        state.ImportLinks["s"] = new ImportIdentityLink
        {
            LocalPaperId = "p1",
            Type = "note",
            LocalFingerprint = "fp",
            PreviousLocalPaperIds = ["p0"]
        };
        var papers = new Dictionary<string, PaperSnapshot>
        {
            ["p1"] = new("p1", "note", "笔记", true, false, false, "builtin.markdown"),
            ["p0"] = new("p0", "note", "旧副本", true, false, false, "builtin.markdown")
        };

        var service = new ImportIdentityService(null!, state);
        service.Reconcile(papers);

        Assert.False(service.HasChanges);
        Assert.Equal(["p0"], state.ImportLinks["s"].PreviousLocalPaperIds);
    }

    [Fact]
    public void Trim_drops_oldest_links_beyond_cap()
    {
        var links = new Dictionary<string, ImportIdentityLink>(StringComparer.Ordinal);
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < DataIoState.MaxImportLinks + 2; i++)
        {
            links[$"s{i}"] = new ImportIdentityLink
            {
                LocalPaperId = $"p{i}",
                Type = "todo",
                LastImportedAt = start.AddSeconds(i).ToString("O", CultureInfo.InvariantCulture)
            };
        }

        Assert.True(ImportIdentityService.Trim(links));
        Assert.Equal(DataIoState.MaxImportLinks, links.Count);
        Assert.False(links.ContainsKey("s0"));
        Assert.False(links.ContainsKey("s1"));
        Assert.True(links.ContainsKey("s2"));
    }
}
