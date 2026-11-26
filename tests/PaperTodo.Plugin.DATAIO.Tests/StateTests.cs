using Xunit;

namespace PaperTodo.Plugin.DataIo.Tests;

/// <summary>插件状态的容错解析与映射归一化。</summary>
public sealed class StateTests
{
    [Fact]
    public void Corrupt_state_is_reported_and_reset()
    {
        var state = DataIoState.Parse("{ this is not json", out var recovered);

        Assert.True(recovered);
        Assert.Empty(state.ImportLinks);
        Assert.Equal("", state.Search);
    }

    [Fact]
    public void Search_keyword_is_restored()
    {
        var state = DataIoState.Parse("{\"Search\":\"周报\"}");

        Assert.Equal("周报", state.Search);
    }

    [Fact]
    public void Import_links_drop_invalid_entries_and_cap_size()
    {
        var links = new Dictionary<string, ImportIdentityLink>
        {
            ["ok"] = new() { LocalPaperId = "p1", Type = "todo" },
            ["bad-type"] = new() { LocalPaperId = "p2", Type = "webpage" },
            ["bad-id"] = new() { LocalPaperId = "", Type = "note" },
            ["null"] = null!
        };

        var normalized = DataIoState.NormalizeImportLinks(links);

        Assert.True(normalized.ContainsKey("ok"));
        Assert.False(normalized.ContainsKey("bad-type"));
        Assert.False(normalized.ContainsKey("bad-id"));
        Assert.Equal("", normalized["ok"].LocalFingerprint);
    }

    [Fact]
    public void Plan_filter_preserves_format_capabilities()
    {
        // Markdown 计划标记为“不携带提醒/关联”；按勾选范围生成新计划时必须保留，
        // 否则更新会按“文件中没有”清除本地关联与提醒。
        var plan = new ImportPlan
        {
            SupportsPaperLinks = false,
            SupportsReminders = false,
            ContainerFormat = "Markdown 压缩包"
        };
        plan.Papers.Add(new ImportPaper
        {
            Source = new ExportPaper { Type = "todo", Title = "T", Todos = [] }
        });

        var filtered = plan.WithPapers(plan.Papers);

        Assert.False(filtered.SupportsPaperLinks);
        Assert.False(filtered.SupportsReminders);
        Assert.Equal("Markdown 压缩包", filtered.ContainerFormat);
        Assert.Single(filtered.Papers);
    }

    [Fact]
    public void Migrate_keeps_corrupt_state_for_session_backup()
    {
        const string corrupt = "{ this is not json";

        var migrated = DataIoState.Migrate(corrupt, fromVersion: 0);

        // 原样返回，让会话层按“损坏状态”备份并提示，而不是静默重置。
        Assert.Equal(corrupt, migrated);
    }

    [Fact]
    public void Migrate_normalizes_unknown_values()
    {
        var migrated = DataIoState.Parse(DataIoState.Migrate(
            "{\"ExportFormat\":\"pdf\",\"Search\":null}",
            fromVersion: 0));

        Assert.Equal("json", migrated.ExportFormat);
        Assert.Equal("", migrated.Search);
    }

    [Fact]
    public void History_drops_invalid_entries_and_keeps_newest_first()
    {
        var history = new List<DataIoHistoryEntry>
        {
            new() { Kind = "export", At = "2026-01-01T10:00:00+08:00", Papers = 2 },
            new() { Kind = "bogus", At = "2026-01-02T10:00:00+08:00" },
            new() { Kind = "import", At = "not-a-date" },
            new() { Kind = "import", At = "2026-01-03T10:00:00+08:00", Created = 1, Updated = 2 }
        };

        var normalized = DataIoState.NormalizeHistory(history);

        Assert.Equal(2, normalized.Count);
        Assert.Equal(DataIoHistoryKinds.Import, normalized[0].Kind);
        Assert.Equal(2, normalized[0].Updated);
        Assert.Equal(DataIoHistoryKinds.Export, normalized[1].Kind);
    }

    [Fact]
    public void History_is_capped_and_clamps_counts()
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var history = Enumerable.Range(0, DataIoState.MaxHistory + 5)
            .Select(index => new DataIoHistoryEntry
            {
                Kind = DataIoHistoryKinds.Export,
                At = start.AddMinutes(-index).ToString("O"),
                Papers = index == 5 ? -3 : index
            })
            .ToList();

        var normalized = DataIoState.NormalizeHistory(history);

        Assert.Equal(DataIoState.MaxHistory, normalized.Count);
        Assert.Equal(0, normalized[0].Papers);
        Assert.Equal(0, normalized[5].Papers);
        Assert.All(normalized, entry => Assert.True(entry.Papers >= 0));
    }

    [Fact]
    public void History_round_trips_without_computed_properties()
    {
        var state = new DataIoState();
        state.History.Add(new DataIoHistoryEntry
        {
            Kind = DataIoHistoryKinds.Export,
            At = "2026-01-01T10:00:00+08:00",
            FilePath = "a.json",
            Format = "json",
            Papers = 2
        });

        var json = DataIoState.Serialize(state);

        Assert.DoesNotContain("IsExport", json);
        var parsed = DataIoState.Parse(json);
        Assert.Single(parsed.History);
        Assert.True(parsed.History[0].IsExport);
    }

    [Fact]
    public void Migrate_preserves_history()
    {
        var migrated = DataIoState.Parse(DataIoState.Migrate(
            "{\"History\":[{\"Kind\":\"export\",\"At\":\"2026-01-01T10:00:00+08:00\",\"Papers\":3}]}",
            fromVersion: 0));

        Assert.Single(migrated.History);
        Assert.Equal(3, migrated.History[0].Papers);
    }
}
