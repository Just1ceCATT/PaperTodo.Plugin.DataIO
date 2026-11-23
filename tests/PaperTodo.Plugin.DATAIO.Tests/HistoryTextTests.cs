using Xunit;

namespace PaperTodo.Plugin.DataIo.Tests;

/// <summary>悬停面板历史记录的展示文案。</summary>
public sealed class HistoryTextTests
{
    private static readonly TimeSpan Offset = TimeSpan.FromHours(8);
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 15, 0, 0, Offset);

    [Fact]
    public void Relative_time_handles_minutes_today_and_yesterday()
    {
        Assert.Equal("刚刚", DataIoHistoryText.FormatRelativeTime(Now.AddSeconds(-30), Now));
        Assert.Equal("5 分钟前", DataIoHistoryText.FormatRelativeTime(Now.AddMinutes(-5), Now));
        Assert.Equal("今天 12:00", DataIoHistoryText.FormatRelativeTime(Now.AddHours(-3), Now));
        Assert.Equal("昨天 19:00", DataIoHistoryText.FormatRelativeTime(Now.AddHours(-20), Now));
    }

    [Fact]
    public void Relative_time_falls_back_to_date_outside_yesterday()
    {
        Assert.Equal(
            "10-02 09:05",
            DataIoHistoryText.FormatRelativeTime(
                new DateTimeOffset(2026, 10, 2, 9, 5, 0, Offset),
                Now));
        Assert.Equal(
            "2025-12-31 08:30",
            DataIoHistoryText.FormatRelativeTime(
                new DateTimeOffset(2025, 12, 31, 8, 30, 0, Offset),
                Now));
    }

    [Fact]
    public void Export_title_and_badge_split_count_from_label()
    {
        var entry = new DataIoHistoryEntry
        {
            Kind = DataIoHistoryKinds.Export,
            Papers = 3
        };

        Assert.Equal("导出", DataIoHistoryText.MiniTitle(entry));
        var badge = Assert.Single(DataIoHistoryText.Badges(entry));
        Assert.Equal("3 张纸片", badge.Text);
        Assert.Equal(DataIoHistoryBadgeTone.Accent, badge.Tone);
    }

    [Fact]
    public void Import_badges_use_success_skip_failure_wording()
    {
        var entry = new DataIoHistoryEntry
        {
            Kind = DataIoHistoryKinds.Import,
            Created = 2,
            Updated = 1,
            Skipped = 2,
            Failed = 1
        };

        var badges = DataIoHistoryText.Badges(entry);

        Assert.Equal("导入", DataIoHistoryText.MiniTitle(entry));
        Assert.Equal(
            ["成功 3", "跳过 2", "失败 1"],
            badges.Select(badge => badge.Text).ToArray());
        Assert.Equal(
            [
                DataIoHistoryBadgeTone.Accent,
                DataIoHistoryBadgeTone.Neutral,
                DataIoHistoryBadgeTone.Warning
            ],
            badges.Select(badge => badge.Tone).ToArray());
    }

    [Fact]
    public void Cancelled_import_keeps_partial_success_badge()
    {
        var entry = new DataIoHistoryEntry
        {
            Kind = DataIoHistoryKinds.Import,
            Created = 1,
            Cancelled = true
        };

        Assert.Equal("导入中断", DataIoHistoryText.MiniTitle(entry));
        Assert.Equal("成功 1", Assert.Single(DataIoHistoryText.Badges(entry)).Text);
    }

    [Fact]
    public void Mini_subtitle_includes_format_and_file_name()
    {
        var entry = new DataIoHistoryEntry
        {
            Kind = DataIoHistoryKinds.Export,
            At = Now.AddMinutes(-5).ToString("O"),
            FilePath = @"C:\Users\TT\Documents\papertodo-export.json",
            Format = "json"
        };

        Assert.Equal("JSON · papertodo-export.json", DataIoHistoryText.MiniSubtitle(entry));
    }

    [Fact]
    public void Warnings_do_not_create_badges()
    {
        var entry = new DataIoHistoryEntry
        {
            Kind = DataIoHistoryKinds.Import,
            Warnings = 2
        };

        Assert.Equal("无变化", Assert.Single(DataIoHistoryText.Badges(entry)).Text);
    }

    [Fact]
    public void Compact_file_name_keeps_head_and_extension()
    {
        var compact = DataIoHistoryText.CompactFileName(
            @"C:\Users\TT\Documents\2026-annual-project-final-version.json",
            20);

        Assert.EndsWith(".json", compact);
        Assert.Contains("…", compact);
        Assert.True(TitleSanitizer.DisplayWidth(compact) <= 20);
    }

    [Fact]
    public void Export_operation_detail_includes_file_name_format_and_count()
    {
        var entry = new DataIoHistoryEntry
        {
            Kind = DataIoHistoryKinds.Export,
            FilePath = @"C:\Users\TT\Documents\pt-261123-1435-3张.json",
            Format = "json",
            Papers = 3
        };

        Assert.Equal(
            "pt-261123-1435-3张.json · JSON · 3 张纸片",
            DataIoHistoryText.OperationDetail(entry));
    }

    [Fact]
    public void Import_operation_detail_puts_file_name_first()
    {
        var entry = new DataIoHistoryEntry
        {
            Kind = DataIoHistoryKinds.Import,
            FilePath = @"C:\Users\TT\Documents\update-diff-v2.json",
            Format = "json",
            Created = 2,
            Updated = 1
        };

        Assert.Equal(
            "update-diff-v2.json · JSON · 成功 3",
            DataIoHistoryText.OperationDetail(entry));
    }

    [Fact]
    public void Missing_file_path_has_readable_placeholder()
    {
        Assert.Equal("未记录文件", DataIoHistoryText.FileName(null));
        Assert.Equal("未记录文件", DataIoHistoryText.FileName("  "));
    }
}
