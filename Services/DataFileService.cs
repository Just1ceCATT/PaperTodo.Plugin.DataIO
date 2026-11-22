using System.Text.Json;

namespace PaperTodo.Plugin.DataIo;

/// <summary>PaperTodo JSON 数据文件的读写。</summary>
internal static class DataFileService
{
    public static void Write(string path, ExportDocument document)
    {
        var json = JsonSerializer.Serialize(document, ExportJson.Options);
        JsonFiles.WriteAllTextAtomic(path, json);
    }

    public static string ReadPayload(string path) => JsonFiles.ReadAllText(path);
}
