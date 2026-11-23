using Xunit;

namespace PaperTodo.Plugin.DataIo.Tests;

/// <summary>插件设置解析：缺失或异常值回退默认值。</summary>
public sealed class DataIoSettingsTests
{
    [Fact]
    public void Delete_missing_todos_defaults_to_enabled()
    {
        Assert.True(DataIoSettings.Parse(null).DeleteMissingTodos);
        Assert.True(DataIoSettings.Parse("{}").DeleteMissingTodos);
    }

    [Fact]
    public void Delete_missing_todos_can_be_disabled()
    {
        Assert.False(DataIoSettings.Parse("{\"deleteMissingTodos\":false}").DeleteMissingTodos);
    }

    [Fact]
    public void Non_boolean_value_falls_back_to_default()
    {
        Assert.True(DataIoSettings.Parse("{\"deleteMissingTodos\":\"no\"}").DeleteMissingTodos);
    }
}
