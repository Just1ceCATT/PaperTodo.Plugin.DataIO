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
    public void NoOp_note_is_only_detected_for_mapped_unchanged_notes()
    {
        static ImportPaper Paper(string type, string? target, bool hasChanges) => new()
        {
            Source = new ExportPaper { Type = type },
            UpdateTargetId = target,
            UpdateHasChanges = hasChanges
        };

        Assert.True(DataIoSession.IsNoOpUpdate(Paper("note", "p1", hasChanges: false)));
        Assert.False(DataIoSession.IsNoOpUpdate(Paper("note", "p1", hasChanges: true)));
        Assert.False(DataIoSession.IsNoOpUpdate(Paper("note", null, hasChanges: false)));
        // 待办仍保留在预览中：差异快照不包含提醒/关联，不能据此判定无操作。
        Assert.False(DataIoSession.IsNoOpUpdate(Paper("todo", "p1", hasChanges: false)));
    }

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
}
