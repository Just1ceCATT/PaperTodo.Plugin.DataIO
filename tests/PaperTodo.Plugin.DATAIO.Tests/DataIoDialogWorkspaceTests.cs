using System.Windows;
using System.Windows.Controls;
using PaperTodo.Plugin;
using Xunit;

namespace PaperTodo.Plugin.DataIo.Tests;

/// <summary>备份工作区选择框的默认勾选行为。</summary>
public sealed class DataIoDialogWorkspaceTests
{
    [Fact]
    public void Workspace_picker_preselects_current_workspace()
    {
        Exception? failure = null;
        bool[]? states = null;
        var thread = new Thread(() =>
        {
            try
            {
                var theme = new PaperBodyTheme(
                    true,
                    "#211F1C",
                    "#E7E0D4",
                    "#92897B",
                    "#A88E6A",
                    "#4C453D",
                    "Microsoft YaHei UI",
                    1.0);
                var dialog = new DataIoDialog(
                    theme,
                    System.Windows.Media.Colors.Gray,
                    "选择备份工作区",
                    "导入所选",
                    "取消");
                dialog.SetCoreBackupWorkspaces(
                    "PaperTodo-core-test.zip",
                    [
                        new CoreBackupWorkspace(1, "data.json", 8, 7, 1, 5, true),
                        new CoreBackupWorkspace(2, "data2.json", 8, 4, 4, 3, true)
                    ],
                    "data2.json");

                var boxes = FindCheckBoxes(dialog).ToArray();
                states = boxes.Select(box => box.IsChecked == true).ToArray();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "对话框构建超时");
        Assert.Null(failure);
        Assert.NotNull(states);
        Assert.Equal([false, true], states);
    }

    [Fact]
    public void Workspace_picker_selects_all_when_current_is_absent()
    {
        Exception? failure = null;
        bool[]? states = null;
        var thread = new Thread(() =>
        {
            try
            {
                var theme = new PaperBodyTheme(
                    true,
                    "#211F1C",
                    "#E7E0D4",
                    "#92897B",
                    "#A88E6A",
                    "#4C453D",
                    "Microsoft YaHei UI",
                    1.0);
                var dialog = new DataIoDialog(
                    theme,
                    System.Windows.Media.Colors.Gray,
                    "选择备份工作区",
                    "导入所选",
                    "取消");
                dialog.SetCoreBackupWorkspaces(
                    "PaperTodo-core-test.zip",
                    [
                        new CoreBackupWorkspace(1, "data.json", 8, 7, 1, 5, true),
                        new CoreBackupWorkspace(2, "data2.json", 8, 4, 4, 3, true)
                    ],
                    "data3.json");

                states = FindCheckBoxes(dialog)
                    .Select(box => box.IsChecked == true)
                    .ToArray();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "对话框构建超时");
        Assert.Null(failure);
        Assert.NotNull(states);
        Assert.Equal([true, true], states);
    }

    [Fact]
    public void Workspace_picker_does_not_select_others_when_current_is_unreadable()
    {
        // 当前工作区条目存在但不可读：不应退化为勾选其它可读工作区。
        var states = BuildPickerStates(
            [
                new CoreBackupWorkspace(1, "data.json", 0, 0, 0, 0, false),
                new CoreBackupWorkspace(2, "data2.json", 8, 4, 4, 3, true)
            ],
            "data.json");

        Assert.Equal([false, false], states);
    }

    [Fact]
    public void Workspace_picker_selects_nothing_when_current_is_unknown()
    {
        // 无法判断当前工作区（null）时不预勾选，避免默认合并全部工作区。
        var states = BuildPickerStates(
            [
                new CoreBackupWorkspace(1, "data.json", 8, 7, 1, 5, true),
                new CoreBackupWorkspace(2, "data2.json", 8, 4, 4, 3, true)
            ],
            null);

        Assert.Equal([false, false], states);
    }

    [Fact]
    public void Update_preview_renders_in_batches_and_covers_all_papers()
    {
        Exception? failure = null;
        (int Choices, string? FirstMore, string? SecondMore, bool LastMoreMissing)? state = null;
        var thread = new Thread(() =>
        {
            try
            {
                var theme = new PaperBodyTheme(
                    true,
                    "#211F1C",
                    "#E7E0D4",
                    "#92897B",
                    "#A88E6A",
                    "#4C453D",
                    "Microsoft YaHei UI",
                    1.0);
                var dialog = new DataIoDialog(
                    theme,
                    System.Windows.Media.Colors.Gray,
                    "更新预览",
                    "导入",
                    "取消");
                var papers = Enumerable.Range(0, 100)
                    .Select(index => new ImportPaper
                    {
                        Source = new ExportPaper
                        {
                            Type = "note",
                            Title = $"笔记 {index}",
                            Content = "正文",
                            ContentAvailable = true
                        },
                        UpdateTargetId = $"p{index}",
                        UpdateTargetTitle = $"本地 {index}"
                    })
                    .ToList();

                dialog.SetUpdatePreview(papers, null, canCreate: true);

                var firstMore = FindButtons(dialog)
                    .FirstOrDefault(button => $"{button.Content}".Contains("显示更多"));
                firstMore?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var secondMore = FindButtons(dialog)
                    .FirstOrDefault(button => $"{button.Content}".Contains("显示更多"));
                secondMore?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var lastMore = FindButtons(dialog)
                    .FirstOrDefault(button => $"{button.Content}".Contains("显示更多"));

                state = (
                    dialog.UpdateChoices.Count,
                    firstMore is null ? null : $"{firstMore.Content}",
                    secondMore is null ? null : $"{secondMore.Content}",
                    lastMore is null);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "对话框构建超时");
        Assert.Null(failure);
        Assert.NotNull(state);
        Assert.Equal(100, state!.Value.Choices);
        Assert.Contains("剩余 60 张", state.Value.FirstMore);
        Assert.Contains("剩余 20 张", state.Value.SecondMore);
        Assert.True(state.Value.LastMoreMissing);
    }

    private static IEnumerable<Button> FindButtons(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is Button button)
            {
                yield return button;
            }

            if (child is DependencyObject dependencyObject)
            {
                foreach (var nested in FindButtons(dependencyObject))
                {
                    yield return nested;
                }
            }
        }
    }

    private static bool[] BuildPickerStates(
        IReadOnlyList<CoreBackupWorkspace> workspaces,
        string? currentWorkspaceName)
    {
        bool[]? states = null;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var theme = new PaperBodyTheme(
                    true,
                    "#211F1C",
                    "#E7E0D4",
                    "#92897B",
                    "#A88E6A",
                    "#4C453D",
                    "Microsoft YaHei UI",
                    1.0);
                var dialog = new DataIoDialog(
                    theme,
                    System.Windows.Media.Colors.Gray,
                    "选择备份工作区",
                    "导入所选",
                    "取消");
                dialog.SetCoreBackupWorkspaces(
                    "PaperTodo-core-test.zip",
                    workspaces,
                    currentWorkspaceName);
                states = FindCheckBoxes(dialog)
                    .Select(box => box.IsChecked == true)
                    .ToArray();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "对话框构建超时");
        Assert.Null(failure);
        Assert.NotNull(states);
        return states;
    }

    private static IEnumerable<CheckBox> FindCheckBoxes(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is CheckBox checkBox)
            {
                yield return checkBox;
            }

            if (child is DependencyObject dependencyObject)
            {
                foreach (var nested in FindCheckBoxes(dependencyObject))
                {
                    yield return nested;
                }
            }
        }
    }
}
