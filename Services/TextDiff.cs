namespace PaperTodo.Plugin.DataIo;

/// <summary>
/// 一行差异：' ' 表示相同，'-' 表示本地有而文件没有，'+' 表示文件新增，'…' 表示省略的上下文。
/// OldLine/NewLine 为 1 基行号，0 表示该侧没有对应行。
/// </summary>
internal readonly record struct DiffLine(char Kind, string Text, int OldLine, int NewLine);

/// <summary>
/// 轻量行级差异：先求最长公共子序列，再按 Git 的阅读习惯组织差异块。
/// 未变化的内容作为上下文保留；删除和新增紧邻展示；多处修改分别形成差异块，
/// 相距较远的差异块之间用省略行分隔。对超长内容做截断，避免预览窗口消耗过多内存。
/// </summary>
internal static class TextDiff
{
    /// <summary>每个差异块在变化行前后保留的上下文行数。</summary>
    private const int ContextLines = 2;

    /// <summary>预览中最多展示的差异行数；超出时截断并提示。</summary>
    private const int MaxLines = 160;

    /// <summary>LCS 动态规划允许的最大单元格数；超过后退化为整块替换。</summary>
    private const long MaxLcsCells = 2_000_000;

    public static IReadOnlyList<DiffLine> Compute(
        string? before,
        string? after,
        CancellationToken cancellationToken = default)
    {
        var oldLines = Split(before);
        var newLines = Split(after);

        var operations = BuildOperations(oldLines, newLines, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return BuildHunks(operations);
    }

    public static string TodoSnapshot(IEnumerable<(string Text, bool Done)> items)
    {
        return string.Join(
            "\n",
            items.Select(item => (item.Done ? "- [x] " : "- [ ] ") + item.Text));
    }

    /// <summary>
    /// 生成完整的行级操作序列（相同、删除、新增）。先剥离公共前缀/后缀，
    /// 只对中间部分求 LCS；中间过大时退化为“先删后加”，避免占用过多内存。
    /// </summary>
    private static List<DiffLine> BuildOperations(
        string[] oldLines,
        string[] newLines,
        CancellationToken cancellationToken)
    {
        var prefix = 0;
        while (prefix < oldLines.Length &&
               prefix < newLines.Length &&
               string.Equals(oldLines[prefix], newLines[prefix], StringComparison.Ordinal))
        {
            prefix++;
        }

        var suffix = 0;
        while (suffix < oldLines.Length - prefix &&
               suffix < newLines.Length - prefix &&
               string.Equals(oldLines[^(suffix + 1)], newLines[^(suffix + 1)], StringComparison.Ordinal))
        {
            suffix++;
        }

        var operations = new List<DiffLine>(oldLines.Length + newLines.Length);

        for (var i = 0; i < prefix; i++)
        {
            operations.Add(new DiffLine(' ', oldLines[i], i + 1, i + 1));
        }

        var oldMiddle = oldLines.AsSpan(prefix, oldLines.Length - prefix - suffix);
        var newMiddle = newLines.AsSpan(prefix, newLines.Length - prefix - suffix);
        AppendMiddle(operations, oldMiddle, newMiddle, prefix, cancellationToken);

        for (var i = 0; i < suffix; i++)
        {
            operations.Add(new DiffLine(
                ' ',
                oldLines[oldLines.Length - suffix + i],
                oldLines.Length - suffix + i + 1,
                newLines.Length - suffix + i + 1));
        }

        return operations;
    }

    private static void AppendMiddle(
        List<DiffLine> operations,
        ReadOnlySpan<string> oldMiddle,
        ReadOnlySpan<string> newMiddle,
        int prefix,
        CancellationToken cancellationToken)
    {
        if (oldMiddle.Length == 0)
        {
            for (var j = 0; j < newMiddle.Length; j++)
            {
                operations.Add(new DiffLine('+', newMiddle[j], 0, prefix + j + 1));
            }

            return;
        }

        if (newMiddle.Length == 0)
        {
            for (var i = 0; i < oldMiddle.Length; i++)
            {
                operations.Add(new DiffLine('-', oldMiddle[i], prefix + i + 1, 0));
            }

            return;
        }

        // 中间部分过大时放弃 LCS：按“整块删除 + 整块新增”展示，至少保证内容完整。
        if ((long)oldMiddle.Length * newMiddle.Length > MaxLcsCells)
        {
            AppendReplacement(operations, oldMiddle, newMiddle, prefix);
            return;
        }

        var lcs = BuildLcsTable(oldMiddle, newMiddle, cancellationToken);
        var oldIndex = 0;
        var newIndex = 0;
        while (oldIndex < oldMiddle.Length && newIndex < newMiddle.Length)
        {
            if (string.Equals(oldMiddle[oldIndex], newMiddle[newIndex], StringComparison.Ordinal))
            {
                operations.Add(new DiffLine(
                    ' ',
                    oldMiddle[oldIndex],
                    prefix + oldIndex + 1,
                    prefix + newIndex + 1));
                oldIndex++;
                newIndex++;
                continue;
            }

            // 删除优先：同一处修改先展示旧行，再展示新行，接近 Git Diff 的阅读顺序。
            if (lcs[oldIndex + 1, newIndex] >= lcs[oldIndex, newIndex + 1])
            {
                operations.Add(new DiffLine('-', oldMiddle[oldIndex], prefix + oldIndex + 1, 0));
                oldIndex++;
            }
            else
            {
                operations.Add(new DiffLine('+', newMiddle[newIndex], 0, prefix + newIndex + 1));
                newIndex++;
            }
        }

        while (oldIndex < oldMiddle.Length)
        {
            operations.Add(new DiffLine('-', oldMiddle[oldIndex], prefix + oldIndex + 1, 0));
            oldIndex++;
        }

        while (newIndex < newMiddle.Length)
        {
            operations.Add(new DiffLine('+', newMiddle[newIndex], 0, prefix + newIndex + 1));
            newIndex++;
        }
    }

    /// <summary>退化为整块替换：先列出所有旧行，再列出所有新行。</summary>
    private static void AppendReplacement(
        List<DiffLine> operations,
        ReadOnlySpan<string> oldMiddle,
        ReadOnlySpan<string> newMiddle,
        int prefix)
    {
        for (var i = 0; i < oldMiddle.Length; i++)
        {
            operations.Add(new DiffLine('-', oldMiddle[i], prefix + i + 1, 0));
        }

        for (var j = 0; j < newMiddle.Length; j++)
        {
            operations.Add(new DiffLine('+', newMiddle[j], 0, prefix + j + 1));
        }
    }

    /// <summary>lcs[i, j] 表示 old[i..] 与 new[j..] 的最长公共子序列长度。</summary>
    private static int[,] BuildLcsTable(
        ReadOnlySpan<string> oldMiddle,
        ReadOnlySpan<string> newMiddle,
        CancellationToken cancellationToken)
    {
        var rows = oldMiddle.Length;
        var columns = newMiddle.Length;
        var lcs = new int[rows + 1, columns + 1];
        for (var i = rows - 1; i >= 0; i--)
        {
            // 关闭纸片后取消需要尽快生效；2M 单元格的循环也要可中断。
            cancellationToken.ThrowIfCancellationRequested();
            for (var j = columns - 1; j >= 0; j--)
            {
                lcs[i, j] = string.Equals(oldMiddle[i], newMiddle[j], StringComparison.Ordinal)
                    ? lcs[i + 1, j + 1] + 1
                    : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        return lcs;
    }

    /// <summary>
    /// 把操作序列切成差异块：每个变化行前后保留上下文；相距较近的差异块合并，
    /// 相距较远的差异块之间插入“省略 N 行”，避免把整篇正文都塞进预览。
    /// </summary>
    private static IReadOnlyList<DiffLine> BuildHunks(List<DiffLine> operations)
    {
        var changed = new List<int>();
        for (var i = 0; i < operations.Count; i++)
        {
            if (operations[i].Kind is '-' or '+')
            {
                changed.Add(i);
            }
        }

        // 没有变化时不生成差异块，直接返回内容（调用方会显示“内容无变化”）。
        if (changed.Count == 0)
        {
            return operations.Count > MaxLines
                ? operations.Take(MaxLines).Append(new DiffLine('…', "差异已截断", 0, 0)).ToList()
                : operations;
        }

        var hunks = new List<(int Start, int End)>();
        foreach (var index in changed)
        {
            var start = Math.Max(0, index - ContextLines);
            var end = Math.Min(operations.Count - 1, index + ContextLines);
            if (hunks.Count > 0 && start <= hunks[^1].End + 1)
            {
                // 两个差异块之间只剩少量未变化行：合并成一个块，避免碎片化。
                hunks[^1] = (hunks[^1].Start, Math.Max(hunks[^1].End, end));
            }
            else
            {
                hunks.Add((start, end));
            }
        }

        var result = new List<DiffLine>();
        var previousEnd = -1;
        foreach (var (start, end) in hunks)
        {
            if (start > previousEnd + 1)
            {
                result.Add(new DiffLine(
                    '…',
                    $"省略 {start - previousEnd - 1} 行",
                    0,
                    0));
            }

            for (var i = start; i <= end; i++)
            {
                result.Add(operations[i]);
            }

            previousEnd = end;
        }

        if (previousEnd < operations.Count - 1)
        {
            result.Add(new DiffLine(
                '…',
                $"省略 {operations.Count - 1 - previousEnd} 行",
                0,
                0));
        }

        if (result.Count > MaxLines)
        {
            // 只截取开头会让“整段改写”只显示删除行、看不到对应新增，
            // 用户会误以为更新会删掉整篇内容。这里保留头尾两段，中间标注省略。
            var head = MaxLines / 2;
            var tail = MaxLines - head - 1;
            var omitted = result.Count - head - tail;
            result = result
                .Take(head)
                .Append(new DiffLine('…', $"差异已截断，中间省略 {omitted} 行", 0, 0))
                .Concat(result.Skip(result.Count - tail))
                .ToList();
        }

        return result;
    }

    private static string[] Split(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');

        // 末尾换行是行结束符而不是额外的一行空内容：
        // 去掉它可避免“b”与“b\n”之间出现肉眼不可见的新增空行。
        return lines.Length > 1 && lines[^1].Length == 0
            ? lines[..^1]
            : lines;
    }
}
