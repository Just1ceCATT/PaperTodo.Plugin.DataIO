using System.Text.Json;
using PaperTodo.Plugin;
using Xunit;

namespace PaperTodo.Plugin.DataIo.Tests;

/// <summary>provider Runtime 的工作区状态持久化、替换语义与消息应答。</summary>
public sealed class DataIoRuntimeTests
{
    [Fact]
    public void Merge_message_persists_links_and_replies_snapshot()
    {
        var state = new FakeRuntimeState();
        var papers = new FakeRuntimePapers();
        using var runtime = new DataIoRuntime(NewContext(state, papers));

        papers.Raise("tool", RuntimeLinkProtocol.CreateMergeRequest(
            9,
            Workspace(("s1", "p1", "2026-11-24T10:00:00+08:00"))));

        var saved = RuntimeLinkProtocol.ParseState(state.Json, out _);
        Assert.Equal("p1", saved.ImportLinks["s1"].LocalPaperId);

        var reply = Assert.Single(papers.Posted);
        Assert.Equal("tool", reply.PaperId);
        Assert.True(RuntimeLinkProtocol.TryRead(
            reply.Message, out var type, out var requestId, out var workspace, out _));
        Assert.Equal(RuntimeLinkProtocol.SnapshotType, type);
        Assert.Equal(9, requestId);
        Assert.Equal("p1", Assert.Single(workspace!.ImportLinks).Value.LocalPaperId);
    }

    [Fact]
    public void Replace_message_deletes_entries_and_persists_history_and_directories()
    {
        var state = new FakeRuntimeState();
        var papers = new FakeRuntimePapers();
        using var runtime = new DataIoRuntime(NewContext(state, papers));

        papers.Raise("tool", RuntimeLinkProtocol.CreateMergeRequest(
            1,
            Workspace(
                ("s1", "p1", "2026-11-24T10:00:00+08:00"),
                ("s2", "p2", "2026-11-24T10:00:00+08:00"))));
        papers.Raise("tool", RuntimeLinkProtocol.CreateReplace(new RuntimeWorkspaceState
        {
            ImportLinks = new Dictionary<string, ImportIdentityLink>(StringComparer.Ordinal)
            {
                ["s1"] = Link("p1", "2026-11-24T10:00:00+08:00")
            },
            History = [new DataIoHistoryEntry
            {
                Kind = "import",
                At = "2026-11-24T12:00:00+08:00",
                FilePath = "a.json",
                Format = "json"
            }],
            LastImportDirectory = @"D:\import"
        }));

        var saved = RuntimeLinkProtocol.ParseState(state.Json, out _);
        Assert.Single(saved.ImportLinks);
        Assert.False(saved.ImportLinks.ContainsKey("s2"));
        Assert.Single(saved.History);
        Assert.Equal(@"D:\import", saved.LastImportDirectory);
    }

    [Fact]
    public void Older_merge_does_not_override_newer_saved_link()
    {
        var state = new FakeRuntimeState();
        var papers = new FakeRuntimePapers();
        using var runtime = new DataIoRuntime(NewContext(state, papers));

        papers.Raise("tool", RuntimeLinkProtocol.CreateMergeRequest(
            1,
            Workspace(("s1", "new", "2026-11-24T12:00:00+08:00"))));
        papers.Raise("tool", RuntimeLinkProtocol.CreateMergeRequest(
            2,
            Workspace(("s1", "old", "2026-11-24T09:00:00+08:00"))));

        var saved = RuntimeLinkProtocol.ParseState(state.Json, out _);
        Assert.Equal("new", saved.ImportLinks["s1"].LocalPaperId);
    }

    [Fact]
    public void Runtime_state_reloads_after_restart()
    {
        var state = new FakeRuntimeState();
        var papers = new FakeRuntimePapers();

        using (var runtime = new DataIoRuntime(NewContext(state, papers)))
        {
            papers.Raise("tool", RuntimeLinkProtocol.CreateMergeRequest(
                1,
                Workspace(("s1", "p1", "2026-11-24T10:00:00+08:00"))));
        }

        var papers2 = new FakeRuntimePapers();
        using var restored = new DataIoRuntime(NewContext(state, papers2));
        papers2.Raise("tool", RuntimeLinkProtocol.CreateMergeRequest(3, new RuntimeWorkspaceState()));

        var reply = Assert.Single(papers2.Posted);
        Assert.True(RuntimeLinkProtocol.TryRead(
            reply.Message, out _, out _, out var workspace, out _));
        Assert.Equal("p1", Assert.Single(workspace!.ImportLinks).Value.LocalPaperId);
    }

    [Fact]
    public void Corrupt_state_is_reported_once_and_not_kept_in_saved_state()
    {
        const string corrupt = "{\"importLinks\":{\"s1\":";
        var state = new FakeRuntimeState { Json = corrupt };
        var papers = new FakeRuntimePapers();
        using var runtime = new DataIoRuntime(NewContext(state, papers));

        // 本次进程仍应告知 Body 发现过损坏；原始内容已另存为独立备份文件。
        papers.Raise("tool", RuntimeLinkProtocol.CreateMergeRequest(
            1,
            Workspace(("s2", "p2", "2026-11-24T10:00:00+08:00"))));
        var reply = Assert.Single(papers.Posted);
        Assert.True(RuntimeLinkProtocol.TryRead(
            reply.Message, out _, out _, out _, out var corruptFlag));
        Assert.True(corruptFlag);

        // 保存后的状态不再内嵌损坏原文，避免每次启动重复告警。
        var saved = RuntimeLinkProtocol.ParseState(state.Json, out var backup);
        Assert.Equal("p2", saved.ImportLinks["s2"].LocalPaperId);
        Assert.Null(backup);
    }

    private static RuntimeWorkspaceState Workspace(
        params (string SourceId, string LocalId, string ImportedAt)[] links) => new()
    {
        ImportLinks = links.ToDictionary(
            item => item.SourceId,
            item => Link(item.LocalId, item.ImportedAt),
            StringComparer.Ordinal)
    };

    private static PaperPluginRuntimeContext NewContext(
        IPaperPluginRuntimeState state,
        IPaperPluginRuntimePapers papers) => new()
    {
        ProviderId = "papertodo.dataio.native",
        ApiVersion = "2.3",
        GrantedPermissions = new HashSet<string>(),
        Workspace = new FakeWorkspace(),
        Settings = new FakeRuntimeSettings(),
        State = state,
        Papers = papers,
        GlobalTopBar = new FakeTopBar(),
        GlobalShortcuts = new FakeShortcuts()
    };

    private static ImportIdentityLink Link(string paperId, string importedAt) => new()
    {
        LocalPaperId = paperId,
        Type = "note",
        Title = "T",
        LocalFingerprint = "fp",
        LastImportedAt = importedAt
    };

    private sealed class FakeRuntimeState : IPaperPluginRuntimeState
    {
        public string Json { get; set; } = "{}";
        public int StateVersion => 1;
        public int TargetStateVersion => 1;

        public void Save(string json) => Json = json;
    }

    private sealed class FakeRuntimeSettings : IPaperPluginRuntimeSettings
    {
        public string Json => "{}";
        public IDisposable Subscribe(Action<string> handler) => new Noop();
    }

    private sealed class FakeTopBar : IPaperGlobalTopBarApi
    {
        public void SetActionHandler(Action<PaperTopBarActionInvocation>? handler)
        {
        }

        public void SetActions(IReadOnlyList<PaperTopBarAction> actions)
        {
        }

        public void Clear()
        {
        }
    }

    private sealed class FakeShortcuts : IPaperGlobalShortcutApi
    {
        public void SetActionHandler(Action<PaperShortcutActionInvocation>? handler)
        {
        }

        public void Clear()
        {
        }
    }

    private sealed class FakeRuntimePapers : IPaperPluginRuntimePapers
    {
        private Action<PaperPluginRuntimeEvent>? _handler;

        public List<(string PaperId, JsonElement Message)> Posted { get; } = [];

        public IReadOnlyList<PaperPluginRuntimePaper> List() => [];

        public PaperPluginRuntimePaper? Get(string paperId) => null;

        public void SetTitle(string paperId, string title)
        {
        }

        public void SetHeaderText(string paperId, string text)
        {
        }

        public void SetCapsulePresentation(string paperId, PaperCapsulePresentation? presentation)
        {
        }

        public bool PostToBody(string paperId, JsonElement message)
        {
            Posted.Add((paperId, message));
            return true;
        }

        public IDisposable Subscribe(Action<PaperPluginRuntimeEvent> handler)
        {
            _handler = handler;
            return new Noop();
        }

        public void Raise(string paperId, JsonElement message) =>
            _handler?.Invoke(new PaperPluginRuntimeEvent(
                PaperPluginRuntimeEventKind.Message,
                paperId,
                message));
    }

    private sealed class FakeWorkspace : IPaperTodoHostApi
    {
        public IReadOnlySet<string> GrantedPermissions { get; } = new HashSet<string>();

        public IReadOnlyList<PaperSnapshot> ListPapers(string? type = null) => [];

        public PaperSnapshot? GetPaper(string paperId) => null;

        public IReadOnlyList<TodoSnapshot> ListTodos(string? paperId = null, bool includeBlank = false) => [];

        public NoteSnapshot? GetNote(string paperId) => null;

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
