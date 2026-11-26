using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Threading;
using PaperTodo.Plugin;
using Xunit;

namespace PaperTodo.Plugin.DataIo.Tests;

/// <summary>
/// 核心 data.json 的导入身份闭环：首次登记 4 条映射（插件正文纸片跳过），再次解析能命中全部更新目标。
/// </summary>
public sealed class ImportIdentityRoundTripTests
{
    private const string CoreJson = """
        {
          "papers": [
            { "id": "core-todo", "type": "todo", "title": "待办", "items": [
                { "text": "任务", "done": false, "order": 0 },
                { "text": "已办", "done": true, "order": 1 }
            ] },
            { "id": "note-a", "type": "note", "title": "笔记A",
              "bodyProviderId": "builtin.markdown", "content": "正文A" },
            { "id": "note-b", "type": "note", "title": "笔记B",
              "bodyProviderId": "builtin.markdown", "content": "正文B" },
            { "id": "note-c", "type": "note", "title": "笔记C",
              "bodyProviderId": "builtin.markdown", "content": "正文C" },
            { "id": "plugin-note", "type": "note", "title": "插件笔记",
              "bodyProviderId": "api.balance.monitor", "content": "" }
          ]
        }
        """;

    [Fact]
    public Task First_import_records_importable_links_and_second_import_hits_them() =>
        // 解析更新目标会把差异与哈希计算放到后台线程，续体会回到调用方的
        // SynchronizationContext。真实宿主在 UI 线程调用本流程，
        // 这里用带消息泵的 STA 线程模拟同样的环境，否则宿主调用会被投递到无人处理的队列。
        RunOnUiThread(async () =>
        {
            var state = new DataIoState();
            var workspace = new FakeWorkspace();
            var gateway = new HostGateway(NewContext(workspace), Dispatcher.CurrentDispatcher);
            var identity = new ImportIdentityService(gateway, state);

            var first = ImportPlanParser.Parse(CoreJson);
            Assert.Null(first.FatalError);
            Assert.Equal(4, first.Papers.Count);

            var localIds = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var paper in first.Papers)
            {
                var localId = "local-" + paper.Source.SourceId;
                localIds[paper.Source.SourceId] = localId;
                if (paper.Source.Type == "todo")
                {
                    workspace.Papers.Add(new PaperSnapshot(localId, "todo", paper.Source.Title, true, false, false, ""));
                    workspace.Todos[localId] = (paper.Source.Todos ?? [])
                        .Select((todo, index) => new TodoSnapshot(
                            localId, paper.Source.Title, "t" + index, todo.Text, todo.Done, todo.Order, null, null, null))
                        .ToList();
                }
                else
                {
                    workspace.Papers.Add(new PaperSnapshot(localId, "note", paper.Source.Title, true, false, false, "builtin.markdown"));
                    workspace.Notes[localId] = new NoteSnapshot(
                        localId, paper.Source.Title, "builtin.markdown", true, paper.Source.Content ?? "");
                }
            }

            await identity.RecordAsync(first.Papers, localIds, CancellationToken.None);

            Assert.Equal(4, state.ImportLinks.Count);
            Assert.All(first.Papers, paper =>
                Assert.Equal(localIds[paper.Source.SourceId], state.ImportLinks[paper.Source.SourceId].LocalPaperId));

            var second = ImportPlanParser.Parse(CoreJson);
            var resolveIdentity = new ImportIdentityService(gateway, state);
            await resolveIdentity.ResolveAsync(second, CancellationToken.None);

            Assert.Equal(4, second.Papers.Count);
            Assert.All(second.Papers, paper =>
                Assert.Equal(localIds[paper.Source.SourceId], paper.UpdateTargetId));
        });

    /// <summary>在带消息泵的 STA 线程上执行异步流程，模拟宿主 UI 线程。</summary>
    private static Task RunOnUiThread(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            _ = dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    await action();
                    completion.SetResult();
                }
                catch (Exception ex)
                {
                    completion.SetException(ex);
                }
                finally
                {
                    dispatcher.InvokeShutdown();
                }
            });
            Dispatcher.Run();
        })
        {
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static PaperBodyContext NewContext(IPaperTodoHostApi workspace) => new()
    {
        ProviderId = "papertodo.dataio.native",
        ApiVersion = "2.3",
        StateJson = "{}",
        StateVersion = 1,
        TargetStateVersion = 1,
        GrantedPermissions = new HashSet<string>
        {
            PaperTodoPermissionNames.PapersRead,
            PaperTodoPermissionNames.TodosRead,
            PaperTodoPermissionNames.NotesRead
        },
        Paper = new PaperBodyPaperContext
        {
            PaperId = "tool",
            SetTitle = _ => { },
            SetHeaderText = _ => { },
            SetCapsulePresentation = _ => { }
        },
        Body = new PaperBodySurfaceContext
        {
            Controls = new FakeControls(),
            Theme = new PaperBodyTheme(false, "#fff", "#000", "#666", "#b07a31", "#807050", "Segoe UI", 1),
            SetInputClaims = _ => { },
            MarkDirty = () => { },
            OpenExternal = _ => { },
            RequestReload = () => { }
        },
        Workspace = workspace,
        Runtime = new FakeRuntime(),
        SaveStateJson = _ => { }
    };

    private sealed class FakeControls : IPaperBodyControls
    {
        public void ApplySelectStyle(ComboBox comboBox, double fontSize)
        {
        }
    }

    private sealed class FakeRuntime : IPaperPluginRuntimeClient
    {
        public bool IsAvailable => false;
        public bool Post(JsonElement message) => false;
    }

    private sealed class FakeWorkspace : IPaperTodoHostApi
    {
        public List<PaperSnapshot> Papers { get; } = [];
        public Dictionary<string, List<TodoSnapshot>> Todos { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, NoteSnapshot> Notes { get; } = new(StringComparer.Ordinal);

        public IReadOnlySet<string> GrantedPermissions { get; } = new HashSet<string>
        {
            PaperTodoPermissionNames.PapersRead,
            PaperTodoPermissionNames.TodosRead,
            PaperTodoPermissionNames.NotesRead
        };

        public IReadOnlyList<PaperSnapshot> ListPapers(string? type = null) =>
            type is null ? Papers : Papers.Where(paper => paper.Type == type).ToArray();

        public PaperSnapshot? GetPaper(string paperId) => Papers.FirstOrDefault(paper => paper.Id == paperId);

        public IReadOnlyList<TodoSnapshot> ListTodos(string? paperId = null, bool includeBlank = false) =>
            paperId is null
                ? Todos.Values.SelectMany(list => list).ToArray()
                : Todos.TryGetValue(paperId, out var todos) ? todos : [];

        public NoteSnapshot? GetNote(string paperId) => Notes.GetValueOrDefault(paperId);

        public PaperMutationResult CreatePaper(CreatePaperRequest request) => throw new NotSupportedException();
        public AppendTodosResult AppendTodos(AppendTodosRequest request) => throw new NotSupportedException();
        public TodoMutationResult UpdateTodo(UpdateTodoRequest request) => throw new NotSupportedException();
        public TodoMutationResult SetTodoReminder(SetTodoReminderRequest request) => throw new NotSupportedException();
        public NoteMutationResult WriteNote(WriteNoteRequest request) => throw new NotSupportedException();
        public DeleteMutationResult DeleteTodo(DeleteTodoRequest request) => throw new NotSupportedException();
        public DeleteMutationResult DeletePaper(string paperId) => throw new NotSupportedException();
        public IDisposable Subscribe(PaperTodoEventFilter filter, Action<PaperTodoEvent> handler) => new Noop();
    }

    private sealed class Noop : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
