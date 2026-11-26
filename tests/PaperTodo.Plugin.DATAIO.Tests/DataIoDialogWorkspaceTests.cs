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
