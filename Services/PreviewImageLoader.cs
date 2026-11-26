using PaperTodo.Plugin;

namespace PaperTodo.Plugin.DataIo;

/// <summary>
/// 为预览窗口准备正文图片：导出模式只读访问 PaperTodo 图片库，导入模式复用压缩包内已读入内存的图片。
/// 预览只是辅助功能，任何单张图片读取失败都不应影响正文显示。
/// </summary>
internal static class PreviewImageLoader
{
    /// <summary>单次预览最多加载的图片数量，避免超大笔记在打开预览时卡顿。</summary>
    internal const int MaxImages = 40;

    /// <summary>导出预览额外复制的图片总字节上限；导入包内的图片与导入计划共享引用，不计入此限。</summary>
    internal const long MaxExportBytes = 32L * 1024 * 1024;

    /// <summary>导出预览：按正文出现顺序读取 PaperTodo 图片库，键统一为 i:图片ID。</summary>
    public static async Task<IReadOnlyDictionary<string, PreviewImage>> LoadExportAsync(
        HostGateway gateway,
        string paperId,
        string content)
    {
        var images = new Dictionary<string, PreviewImage>(StringComparer.Ordinal);

        if (string.IsNullOrEmpty(content) ||
            ImageReferenceScanner.Count(content) == 0 ||
            !gateway.Can(PaperTodoPermissionNames.NotesRead))
        {
            return images;
        }

        // 旧 PaperTodo 没有图片读取能力时直接跳过，正文照常预览。
        var supported = await gateway.OnUiAsync(() =>
        {
            try
            {
                _ = gateway.Context.NoteAssets;
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        });
        if (!supported)
        {
            return images;
        }

        long totalBytes = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reference in ImageReferenceScanner.Enumerate(content))
        {
            if (images.Count >= MaxImages || totalBytes >= MaxExportBytes)
            {
                break;
            }

            if (!seen.Add(reference.ImageId))
            {
                continue;
            }

            try
            {
                var image = await gateway.OnUiAsync(
                    () => gateway.Context.NoteAssets.ReadImage(paperId, reference.ImageId));
                if (image.Bytes.Length == 0 ||
                    totalBytes + image.Bytes.LongLength > MaxExportBytes)
                {
                    continue;
                }

                totalBytes += image.Bytes.LongLength;
                images[$"i:{reference.ImageId}"] = new PreviewImage(image.Mime, image.Bytes);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is PaperTodoPluginException or InvalidOperationException)
            {
                // 图片被清理、归属变化或超出读取上限时跳过，正文中的引用仍按缺失占位显示。
            }

            await HostGateway.YieldAsync();
        }

        return images;
    }

    /// <summary>导入预览：直接复用压缩包中与正文引用对应的图片字节。</summary>
    public static IReadOnlyDictionary<string, PreviewImage> CollectImport(ImportPaper? paper)
    {
        var images = new Dictionary<string, PreviewImage>(StringComparer.Ordinal);
        if (paper is null || paper.Images.Count == 0)
        {
            return images;
        }

        foreach (var image in paper.Images)
        {
            if (images.Count >= MaxImages)
            {
                break;
            }

            var key = paper.UsesRelativeImageLinks
                ? image.File
                : $"i:{image.SourceImageId}";
            if (image.Bytes.Length == 0)
            {
                continue;
            }

            images.TryAdd(key, new PreviewImage(image.Mime, image.Bytes));
        }

        return images;
    }
}
