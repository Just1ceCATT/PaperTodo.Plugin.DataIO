using Xunit;

namespace PaperTodo.Plugin.DataIo.Tests;

/// <summary>PaperTodo 主数据文件（data.json）的导入适配。</summary>
public sealed class CoreDataJsonImportTests
{
    [Fact]
    public void Core_data_json_maps_ids_items_and_visibility()
    {
        const string json = """
            {
              "uiLanguage": "zh-CN",
              "papers": [
                {
                  "id": "core-todo",
                  "type": "todo",
                  "title": "核心待办",
                  "isVisible": false,
                  "items": [
                    { "text": "已完成", "done": true, "order": 0 },
                    {
                      "text": "待办",
                      "done": false,
                      "order": 1,
                      "reminderAt": "2026-12-01T09:00:00+08:00",
                      "linkedNoteId": "core-note"
                    }
                  ]
                },
                {
                  "id": "core-note",
                  "type": "note",
                  "title": "核心笔记",
                  "isVisible": true,
                  "bodyProviderId": "builtin.markdown",
                  "content": "正文"
                }
              ]
            }
            """;

        var plan = ImportPlanParser.Parse(json);

        Assert.Null(plan.FatalError);
        Assert.Equal(2, plan.Papers.Count);

        var todo = Assert.Single(plan.Papers, paper => paper.Source.Type == "todo");
        Assert.Equal("core-todo", todo.Source.SourceId);
        Assert.False(todo.Source.Show);
        Assert.Equal(2, todo.Source.Todos!.Count);
        Assert.True(todo.Source.Todos[0].Done);
        Assert.Equal("core-note", todo.Source.Todos[1].LinkedSourcePaperId);
        Assert.NotNull(todo.Source.Todos[1].ReminderAt);

        var note = Assert.Single(plan.Papers, paper => paper.Source.Type == "note");
        Assert.Equal("core-note", note.Source.SourceId);
        Assert.Equal("正文", note.Source.Content);
        Assert.True(note.Source.ContentAvailable);

        Assert.Contains(plan.Warnings, warning => warning.Contains("data.json"));
    }

    [Fact]
    public void Core_data_json_skips_notes_owned_by_other_plugins()
    {
        const string json = """
            {
              "papers": [
                {
                  "id": "plugin-note",
                  "type": "note",
                  "title": "插件笔记",
                  "bodyProviderId": "api.balance.monitor",
                  "content": ""
                },
                {
                  "id": "markdown-note",
                  "type": "note",
                  "title": "普通笔记",
                  "bodyProviderId": "builtin.markdown",
                  "content": "正文"
                }
              ]
            }
            """;

        var plan = ImportPlanParser.Parse(json);

        Assert.Null(plan.FatalError);
        var paper = Assert.Single(plan.Papers);
        Assert.Equal("markdown-note", paper.Source.SourceId);
        Assert.Contains(plan.Warnings, warning => warning.Contains("已跳过 1 张"));
    }

    [Fact]
    public void Core_note_without_content_is_not_marked_readable()
    {
        const string json = """
            {
              "uiLanguage": "zh-CN",
              "papers": [
                {
                  "id": "note-no-content",
                  "type": "note",
                  "title": "无正文属性",
                  "bodyProviderId": "builtin.markdown"
                }
              ]
            }
            """;

        var plan = ImportPlanParser.Parse(json);

        Assert.Null(plan.FatalError);
        var paper = Assert.Single(plan.Papers);
        Assert.False(paper.Source.ContentAvailable);
        Assert.Equal("", paper.Source.Content);
    }

    [Fact]
    public void Core_items_with_null_entries_are_filtered()
    {
        const string json = """
            {
              "papers": [
                {
                  "id": "todo-null-item",
                  "type": "todo",
                  "title": "含空条目",
                  "items": [null, { "text": "有效", "done": false, "order": 0 }]
                }
              ]
            }
            """;

        var plan = ImportPlanParser.Parse(json);

        Assert.Null(plan.FatalError);
        var paper = Assert.Single(plan.Papers);
        Assert.Equal("有效", Assert.Single(paper.Source.Todos!).Text);
    }

    [Fact]
    public void Export_without_format_is_rejected_instead_of_read_as_core_data()
    {
        const string json = """
            {
              "schemaVersion": 1,
              "papers": [
                {
                  "sourceId": "s1",
                  "type": "todo",
                  "title": "待办",
                  "todos": [{ "text": "任务", "done": false, "order": 0 }]
                }
              ]
            }
            """;

        var plan = ImportPlanParser.Parse(json);

        Assert.NotNull(plan.FatalError);
        Assert.Contains("不是可导入的数据文件", plan.FatalError);
    }

    [Fact]
    public void Comments_and_trailing_commas_are_accepted()
    {
        const string json = """
            {
              // 手工编辑留下的注释
              "papers": [
                {
                  "id": "note-comment",
                  "type": "note",
                  "title": "注释文件",
                  "bodyProviderId": "builtin.markdown",
                  "content": "正文",
                },
              ],
            }
            """;

        var plan = ImportPlanParser.Parse(json);

        Assert.Null(plan.FatalError);
        var paper = Assert.Single(plan.Papers);
        Assert.Equal("正文", paper.Source.Content);
    }

    [Fact]
    public void Unknown_json_shape_is_rejected()
    {
        var plan = ImportPlanParser.Parse("""{"foo":1}""");

        Assert.NotNull(plan.FatalError);
        Assert.Contains("不是可导入的数据文件", plan.FatalError);
    }

    [Fact]
    public void Export_without_schema_version_is_rejected()
    {
        const string json = """
            {
              "format": "papertodo.paper-export",
              "papers": [
                { "sourceId": "a", "type": "note", "title": "A", "content": "正文" }
              ]
            }
            """;

        var plan = ImportPlanParser.Parse(json);

        Assert.NotNull(plan.FatalError);
        Assert.Contains("schemaVersion", plan.FatalError);
    }

    [Fact]
    public void Export_with_unknown_format_is_rejected()
    {
        const string json = """
            {
              "format": "some.other.format",
              "schemaVersion": 1,
              "papers": [
                { "sourceId": "a", "type": "note", "title": "A", "content": "正文" }
              ]
            }
            """;

        var plan = ImportPlanParser.Parse(json);

        Assert.NotNull(plan.FatalError);
        Assert.Contains("不是可导入的数据文件", plan.FatalError);
    }
}
