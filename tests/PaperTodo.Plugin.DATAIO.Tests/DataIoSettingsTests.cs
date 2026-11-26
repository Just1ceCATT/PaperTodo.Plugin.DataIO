using Xunit;

namespace PaperTodo.Plugin.DataIo.Tests;

/// <summary>插件设置解析：缺失或异常值回退默认值。</summary>
public sealed class DataIoSettingsTests
{
    [Fact]
    public void Delete_missing_todos_defaults_to_disabled()
    {
        Assert.False(DataIoSettings.Parse(null).DeleteMissingTodos);
        Assert.False(DataIoSettings.Parse("{}").DeleteMissingTodos);
    }

    [Fact]
    public void Delete_missing_todos_can_be_enabled()
    {
        Assert.True(DataIoSettings.Parse("{\"deleteMissingTodos\":true}").DeleteMissingTodos);
    }

    [Fact]
    public void Collapse_imported_papers_defaults_to_enabled()
    {
        Assert.True(DataIoSettings.Parse(null).CollapseImportedPapers);
        Assert.True(DataIoSettings.Parse("{}").CollapseImportedPapers);
        Assert.False(DataIoSettings.Parse("{\"collapseImportedPapers\":false}").CollapseImportedPapers);
    }

    [Fact]
    public void Invalid_json_is_reported_as_corrupted()
    {
        var settings = DataIoSettings.Parse("{ not json", out var corrupted);

        Assert.True(corrupted);
        Assert.True(settings.UpdateImportedPapers);

        _ = DataIoSettings.Parse("{}", out var validJsonCorrupted);
        Assert.False(validJsonCorrupted);
    }

    [Fact]
    public void Non_boolean_value_falls_back_to_default()
    {
        Assert.False(DataIoSettings.Parse("{\"deleteMissingTodos\":\"no\"}").DeleteMissingTodos);
    }
}
