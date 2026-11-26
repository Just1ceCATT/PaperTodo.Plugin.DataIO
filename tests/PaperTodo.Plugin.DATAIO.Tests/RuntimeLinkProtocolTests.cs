using System.IO;
using System.Text.Json;
using Xunit;

namespace PaperTodo.Plugin.DataIo.Tests;

/// <summary>Runtime 工作区状态协议：序列化、合并、损坏备份与消息往返。</summary>
public sealed class RuntimeLinkProtocolTests
{
    [Fact]
    public void State_json_round_trips_links_history_and_directories()
    {
        var state = new RuntimeWorkspaceState
        {
            ImportLinks = new Dictionary<string, ImportIdentityLink>(StringComparer.Ordinal)
            {
                ["s1"] = Link("p1", "2026-11-24T10:00:00+08:00"),
                ["s2"] = Link("p2", "2026-11-24T11:00:00+08:00")
            },
            History = [Entry("import", "2026-11-24T10:00:00+08:00", "a.json")],
            LastExportDirectory = @"D:\export",
            LastImportDirectory = @"D:\import"
        };

        var restored = RuntimeLinkProtocol.ParseState(
            RuntimeLinkProtocol.SerializeState(state), out var corrupt);

        Assert.Null(corrupt);
        Assert.Equal(2, restored.ImportLinks.Count);
        Assert.Equal("p1", restored.ImportLinks["s1"].LocalPaperId);
        Assert.Equal("p2", restored.ImportLinks["s2"].LocalPaperId);
        Assert.Single(restored.History);
        Assert.Equal(@"D:\export", restored.LastExportDirectory);
        Assert.Equal(@"D:\import", restored.LastImportDirectory);
    }

    [Fact]
    public void Merge_prefers_newer_import_time()
    {
        var target = new RuntimeWorkspaceState
        {
            ImportLinks = new Dictionary<string, ImportIdentityLink>(StringComparer.Ordinal)
            {
                ["s1"] = Link("old", "2026-11-24T10:00:00+08:00")
            }
        };
        var incoming = new RuntimeWorkspaceState
        {
            ImportLinks = new Dictionary<string, ImportIdentityLink>(StringComparer.Ordinal)
            {
                ["s1"] = Link("new", "2026-11-24T12:00:00+08:00"),
                ["s2"] = Link("other", "2026-11-24T09:00:00+08:00")
            }
        };

        var changed = RuntimeLinkProtocol.MergeState(target, incoming);

        Assert.True(changed);
        Assert.Equal("new", target.ImportLinks["s1"].LocalPaperId);
        Assert.Equal("other", target.ImportLinks["s2"].LocalPaperId);
    }

    [Fact]
    public void Merge_does_not_replace_newer_local_link_with_older_remote()
    {
        var target = new RuntimeWorkspaceState
        {
            ImportLinks = new Dictionary<string, ImportIdentityLink>(StringComparer.Ordinal)
            {
                ["s1"] = Link("local", "2026-11-24T12:00:00+08:00")
            }
        };
        var incoming = new RuntimeWorkspaceState
        {
            ImportLinks = new Dictionary<string, ImportIdentityLink>(StringComparer.Ordinal)
            {
                ["s1"] = Link("remote", "2026-11-24T10:00:00+08:00")
            }
        };

        var changed = RuntimeLinkProtocol.MergeState(target, incoming);

        Assert.False(changed);
        Assert.Equal("local", target.ImportLinks["s1"].LocalPaperId);
    }

    [Fact]
    public void Merge_applies_removal_tombstones_and_does_not_resurrect_deleted_links()
    {
        var older = DateTimeOffset.Now.AddMinutes(-10).ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        var removedAt = DateTimeOffset.Now.AddMinutes(-5).ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        var reimportedAt = DateTimeOffset.Now.ToString("O", System.Globalization.CultureInfo.InvariantCulture);

        var target = new RuntimeWorkspaceState
        {
            ImportLinks = new Dictionary<string, ImportIdentityLink>(StringComparer.Ordinal)
            {
                ["s1"] = Link("old", older)
            }
        };
        var incoming = new RuntimeWorkspaceState
        {
            RemovedLinks = new Dictionary<string, string>(StringComparer.Ordinal) { ["s1"] = removedAt }
        };

        Assert.True(RuntimeLinkProtocol.MergeState(target, incoming));
        Assert.False(target.ImportLinks.ContainsKey("s1"));
        Assert.Equal(removedAt, target.RemovedLinks["s1"]);

        // 删除后合入删除前登记的旧映射不能复活。
        Assert.False(RuntimeLinkProtocol.MergeState(target, new RuntimeWorkspaceState
        {
            ImportLinks = new Dictionary<string, ImportIdentityLink>(StringComparer.Ordinal)
            {
                ["s1"] = Link("old", older)
            }
        }));
        Assert.False(target.ImportLinks.ContainsKey("s1"));

        // 删除后重新导入（登记时间更新）可以保留，并清理墓碑。
        Assert.True(RuntimeLinkProtocol.MergeState(target, new RuntimeWorkspaceState
        {
            ImportLinks = new Dictionary<string, ImportIdentityLink>(StringComparer.Ordinal)
            {
                ["s1"] = Link("new", reimportedAt)
            }
        }));
        Assert.Equal("new", target.ImportLinks["s1"].LocalPaperId);
        Assert.False(target.RemovedLinks.ContainsKey("s1"));
    }

    [Fact]
    public void Pending_state_round_trips_removal_tombstones()
    {
        var workspaceFileName = "dataio-tombstone-" + Guid.NewGuid().ToString("N") + ".json";
        try
        {
            var removedAt = DateTimeOffset.Now.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
            var state = new RuntimeWorkspaceState
            {
                RemovedLinks = new Dictionary<string, string>(StringComparer.Ordinal) { ["s1"] = removedAt }
            };

            RuntimeLinkProtocol.WritePendingState(state, workspaceFileName);
            var restored = RuntimeLinkProtocol.TryReadPendingState(workspaceFileName);

            Assert.NotNull(restored);
            Assert.Equal(removedAt, Assert.Single(restored!.RemovedLinks).Value);
        }
        finally
        {
            RuntimeLinkProtocol.DeletePendingState(workspaceFileName);
        }
    }

    [Fact]
    public void Merge_unions_history_and_fills_missing_directories()
    {
        var target = new RuntimeWorkspaceState
        {
            History = [Entry("import", "2026-11-24T10:00:00+08:00", "a.json")]
        };
        var incoming = new RuntimeWorkspaceState
        {
            History =
            [
                Entry("import", "2026-11-24T10:00:00+08:00", "a.json"),
                Entry("export", "2026-11-24T11:00:00+08:00", "b.json")
            ],
            LastImportDirectory = @"D:\import"
        };

        var changed = RuntimeLinkProtocol.MergeState(target, incoming);

        Assert.True(changed);
        Assert.Equal(2, target.History.Count);
        Assert.Equal("export", target.History[0].Kind);
        Assert.Equal(@"D:\import", target.LastImportDirectory);
    }

    [Fact]
    public void Corrupt_state_returns_empty_links_and_keeps_raw_backup()
    {
        const string corrupt = "{\"importLinks\":{\"s1\":";

        var state = RuntimeLinkProtocol.ParseState(corrupt, out var backup);

        Assert.Empty(state.ImportLinks);
        Assert.Equal(corrupt, backup);
    }

    [Fact]
    public void Messages_round_trip()
    {
        var workspace = new RuntimeWorkspaceState
        {
            ImportLinks = new Dictionary<string, ImportIdentityLink>(StringComparer.Ordinal)
            {
                ["s1"] = Link("p1", "2026-11-24T10:00:00+08:00")
            }
        };

        var merge = RuntimeLinkProtocol.CreateMergeRequest(7, workspace);
        Assert.True(RuntimeLinkProtocol.TryRead(
            merge, out var type, out var requestId, out var received, out var corrupt));
        Assert.Equal(RuntimeLinkProtocol.MergeType, type);
        Assert.Equal(7, requestId);
        Assert.False(corrupt);
        Assert.Equal("p1", Assert.Single(received!.ImportLinks).Value.LocalPaperId);

        var snapshot = RuntimeLinkProtocol.CreateSnapshot(7, workspace, corrupt: true);
        Assert.True(RuntimeLinkProtocol.TryRead(
            snapshot, out type, out requestId, out received, out corrupt));
        Assert.Equal(RuntimeLinkProtocol.SnapshotType, type);
        Assert.Equal(7, requestId);
        Assert.True(corrupt);
        Assert.Single(received!.ImportLinks);
    }

    [Fact]
    public void Snapshot_with_null_collections_is_normalized()
    {
        // 外部消息可能把集合写成 null；合并历史/映射时不能让异常逃逸出回调。
        using var document = JsonDocument.Parse(
            "{\"type\":\"" + RuntimeLinkProtocol.SnapshotType +
            "\",\"requestId\":7,\"workspace\":{\"importLinks\":null,\"history\":null}}");

        Assert.True(RuntimeLinkProtocol.TryRead(
            document.RootElement,
            out _,
            out var requestId,
            out var workspace,
            out _));

        Assert.Equal(7, requestId);
        Assert.NotNull(workspace);
        Assert.NotNull(workspace!.ImportLinks);
        Assert.NotNull(workspace.History);
    }

    [Theory]
    [InlineData("data.json", "data")]
    [InlineData("data2.json", "data2")]
    [InlineData(@"C:\PaperTodo\data3.json", "data3")]
    [InlineData("", "default")]
    [InlineData(null, "default")]
    [InlineData("weird name!.json", "weird_name_")]
    public void Workspace_key_isolates_pending_files(string? fileName, string expected)
    {
        Assert.Equal(expected, RuntimeLinkProtocol.WorkspaceKey(fileName));
    }

    [Fact]
    public void Pending_state_round_trips_history_and_directories_per_workspace()
    {
        var workspaceFileName = "dataio-test-" + Guid.NewGuid().ToString("N") + ".json";
        try
        {
            var state = new RuntimeWorkspaceState
            {
                ImportLinks = new Dictionary<string, ImportIdentityLink>(StringComparer.Ordinal)
                {
                    ["s1"] = Link("p1", "2026-11-24T10:00:00+08:00")
                },
                History = [Entry("import", "2026-11-24T10:00:00+08:00", "a.json")],
                LastExportDirectory = @"D:\export",
                LastImportDirectory = @"D:\import"
            };

            RuntimeLinkProtocol.WritePendingState(state, workspaceFileName);
            var restored = RuntimeLinkProtocol.TryReadPendingState(workspaceFileName);

            Assert.NotNull(restored);
            Assert.Equal("p1", Assert.Single(restored!.ImportLinks).Value.LocalPaperId);
            Assert.Single(restored.History);
            Assert.Equal(@"D:\export", restored.LastExportDirectory);
            Assert.Equal(@"D:\import", restored.LastImportDirectory);

            // 不同工作区使用不同日志文件，互不覆盖。
            var otherKey = workspaceFileName + ".other";
            Assert.NotEqual(
                RuntimeLinkProtocol.PendingStatePath(workspaceFileName),
                RuntimeLinkProtocol.PendingStatePath(otherKey));
        }
        finally
        {
            RuntimeLinkProtocol.DeletePendingState(workspaceFileName);
        }
    }

    [Fact]
    public void Corrupt_pending_state_is_backed_up_instead_of_silently_deleted()
    {
        var key = "dataio-pending-" + Guid.NewGuid().ToString("N") + ".json";
        var path = RuntimeLinkProtocol.PendingStatePath(key)!;
        var directory = Path.GetDirectoryName(path)!;
        var backupPath = Path.Combine(
            directory,
            $"state-corrupt.{RuntimeLinkProtocol.WorkspaceKey(key)}.bak");
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(path, "{\"importLinks\":{");

            var state = RuntimeLinkProtocol.TryReadPendingState(key);

            Assert.Null(state);
            Assert.False(File.Exists(path));
            Assert.True(File.Exists(backupPath));
            Assert.Contains("importLinks", File.ReadAllText(backupPath));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            if (File.Exists(backupPath))
            {
                File.Delete(backupPath);
            }
        }
    }

    private static ImportIdentityLink Link(string paperId, string importedAt) => new()
    {
        LocalPaperId = paperId,
        Type = "note",
        Title = "T",
        LocalFingerprint = "fp",
        LastImportedAt = importedAt
    };

    private static DataIoHistoryEntry Entry(string kind, string at, string path) => new()
    {
        Kind = kind,
        At = at,
        FilePath = path,
        Format = "json"
    };
}
