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
    public void Migrate_normalizes_unknown_values()
    {
        var migrated = DataIoState.Parse(DataIoState.Migrate(
            "{\"ExportFormat\":\"pdf\",\"Search\":null}",
            fromVersion: 0));

        Assert.Equal("json", migrated.ExportFormat);
        Assert.Equal("", migrated.Search);
    }
}
