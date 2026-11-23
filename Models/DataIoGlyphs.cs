namespace PaperTodo.Plugin.DataIo;

/// <summary>Segoe Fluent Icons 字形，供正文与悬停面板共用。</summary>
internal static class DataIoGlyphs
{
    /// <summary>导出：从方框中向外指的箭头，避免与“上传”混淆。</summary>
    public static readonly string Export = char.ConvertFromUtf32(0xE72D);

    /// <summary>导入：向界线内指的箭头，表示把外部内容收进本地。</summary>
    public static readonly string Import = char.ConvertFromUtf32(0xE8B5);

    /// <summary>历史记录。</summary>
    public static readonly string History = char.ConvertFromUtf32(0xE81C);
}
