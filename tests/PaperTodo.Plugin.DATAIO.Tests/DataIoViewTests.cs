using System.Windows;
using Xunit;

namespace PaperTodo.Plugin.DataIo.Tests;

/// <summary>正文视图的 XAML 加载与“最近操作”注入。</summary>
public sealed class DataIoViewTests
{
    [Fact]
    public void View_loads_and_accepts_recent_operations()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var view = new DataIoView();
                view.SetRecentOperations(
                [
                    new DataIoHistoryEntry
                    {
                        Kind = DataIoHistoryKinds.Import,
                        At = DateTimeOffset.Now.ToString("O"),
                        FilePath = "update-diff-v2.json",
                        Format = "json",
                        Created = 2,
                        Updated = 1
                    }
                ]);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "视图加载超时");
        Assert.Null(failure);
    }

    [Fact]
    public void History_mode_switches_pages_and_hides_primary_action()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var view = new DataIoView();
                view.SetRecentOperations(
                [
                    new DataIoHistoryEntry
                    {
                        Kind = DataIoHistoryKinds.Export,
                        At = DateTimeOffset.Now.ToString("O"),
                        FilePath = "pt-261123-1435-3张.json",
                        Format = "json",
                        Papers = 3
                    }
                ]);

                // 模拟点击顶部 Tab，验证 Checked 事件已接线。
                view.HistoryModeButton.IsChecked = true;
                Assert.Equal(DataIoMode.History, view.Mode);
                Assert.Equal(Visibility.Visible, view.HistorySection.Visibility);
                Assert.Equal(Visibility.Collapsed, view.ExportImportSection.Visibility);
                Assert.Equal(Visibility.Collapsed, view.PrimaryButton.Visibility);

                view.ExportModeButton.IsChecked = true;
                Assert.Equal(DataIoMode.Export, view.Mode);
                Assert.Equal(Visibility.Collapsed, view.HistorySection.Visibility);
                Assert.Equal(Visibility.Visible, view.ExportImportSection.Visibility);
                Assert.Equal(Visibility.Visible, view.PrimaryButton.Visibility);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "视图切换超时");
        Assert.Null(failure);
    }

    [Fact]
    public void History_row_carries_display_text_and_separator()
    {
        var entry = new DataIoHistoryEntry
        {
            Kind = DataIoHistoryKinds.Export,
            At = DateTimeOffset.Now.AddMinutes(-5).ToString("O"),
            FilePath = @"C:\Users\TT\Documents\pt-261123-1435-3张.json",
            Format = "json",
            Papers = 3
        };

        var row = HistoryRowItem.From(entry, DateTimeOffset.Now, showTopSeparator: true);

        Assert.Equal(DataIoGlyphs.Export, row.Glyph);
        Assert.Equal("导出数据", row.Title);
        Assert.Equal("pt-261123-1435-3张.json · JSON · 3 张纸片", row.Detail);
        Assert.Contains("分钟前", row.TimeText);
        Assert.Equal(entry.FilePath, row.ToolTip);
        Assert.True(row.ShowTopSeparator);
    }
}
