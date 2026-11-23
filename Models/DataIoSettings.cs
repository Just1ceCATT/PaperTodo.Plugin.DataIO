using System.Text.Json;

namespace PaperTodo.Plugin.DataIo;

/// <summary>
/// 宿主绘制的插件设置。字段与 plugin.json 中的设置 id 一一对应，缺失时使用默认值。
/// </summary>
internal sealed class DataIoSettings
{
    public bool AutoStart { get; init; } = true;
    public bool IncludeTodoPapers { get; init; } = true;
    public bool IncludeNotePapers { get; init; } = true;
    public bool ImportShowCreated { get; init; } = true;
    public bool ConfirmBeforeImport { get; init; } = true;
    public bool RestoreOrder { get; init; } = true;
    public bool RestoreLinks { get; init; } = true;
    public bool RestoreReminders { get; init; } = true;
    public bool UpdateImportedPapers { get; init; } = true;

    /// <summary>待办纸片“更新”时是否删除文件中不存在的本地待办。</summary>
    public bool DeleteMissingTodos { get; init; } = true;

    public static DataIoSettings Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new DataIoSettings();
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new DataIoSettings();
            }

            return new DataIoSettings
            {
                AutoStart = ReadBool(root, "autoStart", true),
                IncludeTodoPapers = ReadBool(root, "includeTodoPapers", true),
                IncludeNotePapers = ReadBool(root, "includeNotePapers", true),
                ImportShowCreated = ReadBool(root, "importShowCreated", true),
                ConfirmBeforeImport = ReadBool(root, "confirmBeforeImport", true),
                RestoreOrder = ReadBool(root, "restoreOrder", true),
                RestoreLinks = ReadBool(root, "restoreLinks", true),
                RestoreReminders = ReadBool(root, "restoreReminders", true),
                UpdateImportedPapers = ReadBool(root, "updateImportedPapers", true),
                DeleteMissingTodos = ReadBool(root, "deleteMissingTodos", true)
            };
        }
        catch (JsonException)
        {
            return new DataIoSettings();
        }
    }

    private static bool ReadBool(JsonElement root, string name, bool fallback)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return fallback;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => fallback
        };
    }
}
