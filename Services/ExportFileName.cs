using System.Globalization;
using System.IO;

namespace PaperTodo.Plugin.DataIo;

/// <summary>
/// 导出文件的默认命名：短、按时间可排序，并直接带上纸片数量。
/// 例：pt-261123-1435-3张.json（原 papertodo-export-20261123-143500.json 的紧凑替代）。
/// 用户仍可在保存对话框中改成任意名称。
/// </summary>
internal static class ExportFileName
{
    /// <summary>插件前缀；两字符即可识别来源，同时给时间与数量留出空间。</summary>
    public const string Prefix = "pt";

    public static string Build(DateTimeOffset at, int paperCount, bool markdown)
    {
        var extension = markdown ? ".md" : ".json";
        var count = Math.Max(0, paperCount);
        var stamp = at.ToString("yyMMdd-HHmm", CultureInfo.InvariantCulture);
        return $"{Prefix}-{stamp}-{count}张{extension}";
    }

    /// <summary>含图片时把用户选择的 .json/.md 路径改成 .zip；已经是 .zip 时保持不变。</summary>
    public static string EnsureZipExtension(string path) =>
        string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase)
            ? path
            : Path.ChangeExtension(path, ".zip");
}
