namespace PaperTodo.Plugin.DataIo;

/// <summary>
/// 导入前的确认文案与“无法完整还原”汇总。
/// 纯函数，不依赖宿主状态，便于单独测试与复用。
/// </summary>
internal static class ImportSummaryBuilder
{
    public static string BuildConfirmation(
        IReadOnlyList<ImportPaper> selected,
        ImportPlan sourcePlan)
    {
        var todoPapers = selected.Count(
            paper => string.Equals(paper.Source.Type, "todo", StringComparison.OrdinalIgnoreCase));
        var notePapers = selected.Count - todoPapers;
        var todoItems = selected.Sum(paper => paper.Source.Todos?.Count ?? 0);
        var updatePapers = selected.Count(paper => !string.IsNullOrWhiteSpace(paper.UpdateTargetId));
        var createPapers = selected.Count - updatePapers;
        var localModified = selected.Count(
            paper => paper.UpdateTargetId != null && paper.TargetLocallyModified);

        var lines = new List<string>
        {
            $"将处理 {selected.Count} 张纸片：",
            $"- 新建 {createPapers} 张，更新已有 {updatePapers} 张",
            $"- 待办纸片 {todoPapers} 张（共 {todoItems} 条待办）",
            $"- 笔记纸片 {notePapers} 张",
            ""
        };

        if (updatePapers == 0)
        {
            lines.Add("导入不会覆盖或删除任何现有纸片，导入时会生成新 ID。");
        }
        else
        {
            lines.Add(
                "更新会替换匹配纸片的笔记正文和待办集合，并删除文件中已不存在的本地待办；" +
                "标题和类型保持不变。");
            if (localModified > 0)
            {
                lines.Add($"注意：{localModified} 张匹配到的纸片在本地有修改，更新会覆盖这些修改。");
            }
        }

        var problems = BuildUnrecoverable(selected, sourcePlan);
        if (problems.Count > 0)
        {
            lines.Add("");
            lines.Add("以下内容无法完整还原：");
            lines.AddRange(problems.Select(problem => "- " + problem));
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// 汇总文件层面无法完整还原的内容。导入前确认与更新预览共用同一份说明，
    /// 保证走更新路径时用户同样能看到图片、关联、提醒等损失提示。
    /// </summary>
    public static List<string> BuildUnrecoverable(
        IReadOnlyList<ImportPaper> selected,
        ImportPlan sourcePlan)
    {
        var problems = new List<string>();
        var imageSummary = ImageReferenceSummary.FromNotes(
            selected
                .Where(paper => string.Equals(paper.Source.Type, "note", StringComparison.OrdinalIgnoreCase))
                .Select(paper => paper.Source.Content));
        if (imageSummary.HasAny)
        {
            problems.Add(
                $"{imageSummary.NoteCount} 张笔记包含 {imageSummary.ReferenceCount} 处图片引用" +
                "（跨设备或原图已清理时可能无法显示）");
        }

        if (sourcePlan.LossyImageNotes > 0)
        {
            problems.Add(
                $"{sourcePlan.LossyImageNotes} 张笔记含旧版“图片缺失”标记，更新会破坏图片引用（已强制跳过更新）");
        }

        if (sourcePlan.UnresolvedLinks > 0)
        {
            problems.Add($"{sourcePlan.UnresolvedLinks} 条待办关联的纸片不在本文件中，导入后不会建立关联");
        }

        if (sourcePlan.PathLinkCount > 0)
        {
            problems.Add($"{sourcePlan.PathLinkCount} 条待办关联的文件或文件夹（导入后无法保留）");
        }

        if (sourcePlan.DroppedReminders > 0)
        {
            problems.Add($"{sourcePlan.DroppedReminders} 条已过期提醒");
        }

        if (sourcePlan.TruncatedTitles > 0)
        {
            problems.Add($"{sourcePlan.TruncatedTitles} 个超长标题将被截断");
        }

        return problems;
    }
}
