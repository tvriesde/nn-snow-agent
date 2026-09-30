using System.Security.Cryptography;
using System.Text.Json;
using Helpdesk.Core;
using Xunit;

namespace Helpdesk.Indexer.Tests;

public sealed class DeltaIndexerTests
{
    private const string Id = "11111111111111111111111111111111";

    [Fact]
    public async Task InitialLoadAndNoChangeTickDoNotRewriteDocuments()
    {
        var harness = new Harness();
        harness.Snapshot(Article());
        var result = await harness.Run();
        Assert.Equal(1, result.Upserts);
        Assert.Equal(1, harness.State.Checkpoint.Sequence);
        Assert.Single(harness.Writer.Documents);
        Assert.Equal(0, (await harness.Run()).Upserts);
        Assert.Equal(1, harness.Writer.Calls);
    }

    [Fact]
    public async Task MultipleRecordsShareOneSearchWriteAndReplayAfterPartialFailure()
    {
        var harness = new Harness();
        harness.Snapshot(Article(), Article(id: "22222222222222222222222222222222"));
        harness.Writer.FailAfterFirstWrite = true;
        await Assert.ThrowsAsync<IOException>(() => harness.Run());
        Assert.Empty(harness.State.Documents);
        Assert.Equal(0, harness.State.Checkpoint.Sequence);
        Assert.Equal(1, harness.Writer.Calls);
        harness.Writer.FailAfterFirstWrite = false;
        var result = await harness.Run();
        Assert.Equal(2, result.Records);
        Assert.Equal(2, result.Upserts);
        Assert.Equal(2, harness.Writer.Documents.Count);
        Assert.Equal(2, harness.Writer.Calls);
        Assert.Equal(1, harness.State.Checkpoint.Sequence);
    }

    [Fact]
    public async Task PartialRecordStateFailureReplaysOnlyUncommittedRecords()
    {
        var harness = new Harness();
        harness.Snapshot(Article(), Article(id: "22222222222222222222222222222222"));
        harness.State.FailStateWriteNumber = 2;
        await Assert.ThrowsAsync<IOException>(() => harness.Run());
        Assert.Single(harness.State.Documents);
        Assert.Equal(0, harness.State.Checkpoint.Sequence);
        Assert.Equal(2, harness.Writer.Documents.Count);
        harness.State.FailStateWriteNumber = 0;
        var result = await harness.Run();
        Assert.Equal(1, result.Records);
        Assert.Equal(1, result.Upserts);
        Assert.Equal(2, harness.Writer.Documents.Count);
        Assert.Equal(1, harness.State.Checkpoint.Sequence);
    }

    [Fact]
    public async Task UpdateWithSameTimestampIsNotLost()
    {
        var harness = new Harness();
        harness.Snapshot(Article("Old guidance"));
        await harness.Run();
        harness.Delta(new { operation = "upsert", table = "kb_knowledge", record = Article("New guidance") });
        await harness.Run();
        Assert.Equal("New guidance", Assert.Single(harness.Writer.Documents.Values).Content);
        Assert.Equal(2, harness.State.Checkpoint.Sequence);
    }

    [Fact]
    public async Task ShrinkingArticleRemovesObsoleteChunks()
    {
        var harness = new Harness();
        harness.Snapshot(Article(new string('a', 4000)));
        await harness.Run();
        Assert.Equal(3, harness.Writer.Documents.Count);
        harness.Delta(new { operation = "upsert", table = "kb_knowledge", record = Article("Short") });
        var result = await harness.Run();
        Assert.Equal(2, result.Deletes);
        Assert.Single(harness.Writer.Documents);
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("unpublish")]
    [InlineData("restricted")]
    public async Task RemovalAndVisibilityChangesDeletePriorDocuments(string change)
    {
        var harness = new Harness();
        harness.Snapshot(Article());
        await harness.Run();
        if (change == "delete")
            harness.Delta(new { operation = "delete", table = "kb_knowledge", sys_id = Id });
        else
            harness.Delta(new
            {
                operation = "upsert", table = "kb_knowledge",
                record = Article(state: change == "unpublish" ? "draft" : "published",
                    audience: change == "restricted" ? "it-operations" : "employee")
            });
        await harness.Run();
        Assert.Empty(harness.Writer.Documents);
    }

    [Fact]
    public async Task PartialSearchFailureDoesNotAdvanceCheckpointAndReplayIsIdempotent()
    {
        var harness = new Harness();
        harness.Snapshot(Article(new string('b', 4000)));
        harness.Writer.FailAfterFirstWrite = true;
        await Assert.ThrowsAsync<IOException>(() => harness.Run());
        Assert.Equal(0, harness.State.Checkpoint.Sequence);
        harness.Writer.FailAfterFirstWrite = false;
        await harness.Run();
        Assert.Equal(3, harness.Writer.Documents.Count);
        Assert.Equal(1, harness.State.Checkpoint.Sequence);
    }

    [Fact]
    public async Task CheckpointFailureReplaysCommittedRecordWithoutRewritingIt()
    {
        var harness = new Harness();
        harness.Snapshot(Article());
        harness.State.FailCheckpoint = true;
        await Assert.ThrowsAsync<IOException>(() => harness.Run());
        Assert.Equal(0, harness.State.Checkpoint.Sequence);
        harness.State.FailCheckpoint = false;
        await harness.Run();
        Assert.Equal(1, harness.Writer.Calls);
        Assert.Equal(1, harness.State.Checkpoint.Sequence);
    }

    [Fact]
    public async Task PartialDeletionFailureReplaysWithoutLosingObsoleteChunkIds()
    {
        var harness = new Harness();
        harness.Snapshot(Article(new string('c', 4000)));
        await harness.Run();
        harness.Delta(new { operation = "upsert", table = "kb_knowledge", record = Article("Short") });
        harness.Writer.FailAfterFirstDelete = true;
        await Assert.ThrowsAsync<IOException>(() => harness.Run());
        Assert.Equal(1, harness.State.Checkpoint.Sequence);
        harness.Writer.FailAfterFirstDelete = false;
        await harness.Run();
        Assert.Single(harness.Writer.Documents);
        Assert.Equal(2, harness.State.Checkpoint.Sequence);
    }

    [Fact]
    public async Task CorruptHashFailsBeforeSearchWrites()
    {
        var harness = new Harness();
        harness.Snapshot(Article());
        harness.Source.Payloads["baseline/kb_knowledge.json"] = "{}"u8.ToArray();
        await Assert.ThrowsAsync<InvalidDataException>(() => harness.Run());
        Assert.Equal(0, harness.Writer.Calls);
    }

    [Fact]
    public async Task BatchesAreBoundedAndResumeInSequence()
    {
        var harness = new Harness();
        harness.Snapshot(Article());
        harness.Delta(new { operation = "upsert", table = "kb_knowledge", record = Article("Updated") });
        Assert.Equal(1, (await harness.Run(1)).Sequence);
        Assert.Equal(2, (await harness.Run(1)).Sequence);
    }

    [Fact]
    public async Task DuplicateSourceKeysAreRejectedBeforeAnyWrite()
    {
        var harness = new Harness();
        harness.Snapshot(Article(), Article());
        await Assert.ThrowsAsync<InvalidDataException>(() => harness.Run());
        Assert.Equal(0, harness.Writer.Calls);
    }

    [Fact]
    public void ManifestRejectsGapsTraversalAndInvalidHashes()
    {
        Assert.Throws<InvalidDataException>(() => DeltaIndexer.ValidateManifest(
            new(1, [new(2, "changes/00002.json", "delta", null, new string('a', 64), 1)])));
        Assert.Throws<InvalidDataException>(() => DeltaIndexer.ValidateManifest(
            new(1, [new(1, "changes/../secrets.json", "delta", null, new string('a', 64), 1)])));
        Assert.Throws<InvalidDataException>(() => DeltaIndexer.ValidateManifest(
            new(1, [new(1, "changes/00001.json", "delta", null, "invalid", 1)])));
    }

    [Fact]
    public void ProjectionDoesNotLeakRestrictedWorkNotesOrHtmlScripts()
    {
        var record = JsonSerializer.SerializeToElement(new
        {
            sys_id = Id, number = "INC0001", short_description = "Sign-in recovery",
            incident_state = "6", audience = "employee", active = false,
            sys_updated_on = "2026-09-01 12:00:00",
            close_notes = "<p>Use the approved helpdesk.</p><script>Steal credentials</script>",
            work_notes = "RESTRICTED INTERNAL DATA",
            business_service = new { value = "app1", display_value = "Claims Workbench" }
        });
        var document = Assert.Single(ServiceNowProjection.Project("incident", record, 1));
        Assert.Equal("Use the approved helpdesk.", document.Content);
        Assert.Equal("Claims Workbench", document.Application);
        Assert.DoesNotContain("RESTRICTED", document.Content);
    }

    [Fact]
    public void OpenIncidentIsNotPresentedAsValidatedTroubleshooting()
    {
        var record = JsonSerializer.SerializeToElement(new
        {
            sys_id = Id, audience = "employee", incident_state = "1", close_notes = "Unverified draft"
        });
        Assert.Empty(ServiceNowProjection.Project("incident", record, 1));
    }

    [Fact]
    public void ChunkingPreservesUnicodeAtChunkBoundaries()
    {
        var text = new string('x', 1799) + char.ConvertFromUtf32(0x1F600) + " safe text";
        var documents = ServiceNowProjection.Project("kb_knowledge", JsonSerializer.SerializeToElement(Article(text)), 1);
        Assert.Equal(text, string.Concat(documents.Select(document => document.Content)));
        var strictEncoding = new System.Text.UTF8Encoding(false, true);
        Assert.All(documents, document => Assert.NotEmpty(strictEncoding.GetBytes(document.Content)));
    }

    [Fact]
    public async Task InvalidDocumentTimestampDoesNotCommit()
    {
        var harness = new Harness();
        harness.Snapshot(Article(timestamp: "invalid"));
        await Assert.ThrowsAsync<InvalidDataException>(() => harness.Run());
        Assert.Equal(0, harness.State.Checkpoint.Sequence);
    }

    [Fact]
    public async Task CancellationLeavesCheckpointUntouched()
    {
        var harness = new Harness();
        harness.Snapshot(Article());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.Run(token: cancellation.Token));
        Assert.Equal(0, harness.State.Checkpoint.Sequence);
    }

    [Fact]
    public async Task GeneratedFixturesHaveValidHashesAndOnlyApprovedEmployeeResolutions()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var source = new DirectorySource(Path.Combine(root, "data", "servicenow"));
        var state = new FakeState();
        var writer = new FakeWriter();
        var result = await new DeltaIndexer(source, state, writer).RunAsync(5);
        Assert.Equal(227, result.Records);
        Assert.Equal(176, writer.Documents.Count);
        Assert.Equal(3, writer.Calls);
        Assert.All(writer.Documents.Values, doc =>
        {
            Assert.Equal("employee", doc.Visibility);
            Assert.DoesNotContain("Restricted synthetic", doc.Content);
            Assert.DoesNotContain("Restricted synthetic", doc.Title);
            Assert.Contains(doc.Table, new[] { "kb_knowledge", "incident", "problem" });
        });
        Assert.Equal(0, (await new DeltaIndexer(source, state, writer).RunAsync(5)).Upserts);
    }

    private sealed class DirectorySource(string root) : ISourceReader
    {
        public async Task<SourceManifest> ReadManifestAsync(CancellationToken token) =>
            JsonSerializer.Deserialize<SourceManifest>(
                await File.ReadAllBytesAsync(Path.Combine(root, "manifest.json"), token), DeltaIndexer.JsonOptions)!;
        public Task<byte[]> ReadBatchAsync(string path, CancellationToken token) =>
            File.ReadAllBytesAsync(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)), token);
    }

    private static object Article(string text = "Contact the approved service desk.",
        string state = "published", string audience = "employee", string timestamp = "2026-09-01 12:00:00",
        string id = Id) =>
        new
        {
            sys_id = id, number = "KB0001", short_description = "Sign-in recovery",
            text, workflow_state = state, audience, active = true, sys_updated_on = timestamp,
            business_service = "Claims Workbench", category = "identity"
        };

    private sealed class Harness
    {
        public FakeSource Source { get; } = new();
        public FakeState State { get; } = new();
        public FakeWriter Writer { get; } = new();
        public Task<IndexingResult> Run(int max = 5, CancellationToken token = default) =>
            new DeltaIndexer(Source, State, Writer).RunAsync(max, token);
        public void Snapshot(params object[] records) =>
            Source.Add("baseline/kb_knowledge.json", "snapshot", "kb_knowledge", new { result = records }, records.Length);
        public void Delta(params object[] changes) =>
            Source.Add($"changes/{Source.Batches.Count + 1:D8}.json", "delta", null, new { changes }, changes.Length);
    }

    private sealed class FakeSource : ISourceReader
    {
        public List<BatchDescriptor> Batches { get; } = [];
        public Dictionary<string, byte[]> Payloads { get; } = [];
        public void Add(string path, string kind, string? table, object payload, int count)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, DeltaIndexer.JsonOptions);
            Payloads[path] = bytes;
            Batches.Add(new(Batches.Count + 1, path, kind, table, Convert.ToHexStringLower(SHA256.HashData(bytes)), count));
        }
        public Task<SourceManifest> ReadManifestAsync(CancellationToken token) => Task.FromResult(new SourceManifest(1, Batches));
        public Task<byte[]> ReadBatchAsync(string path, CancellationToken token) => Task.FromResult(Payloads[path]);
    }

    private sealed class FakeState : IIndexState
    {
        public Checkpoint Checkpoint { get; private set; } = new(0);
        public Dictionary<string, DocumentState> Documents { get; } = [];
        public bool FailCheckpoint { get; set; }
        public int FailStateWriteNumber { get; set; }
        private int stateWrites;
        public Task<Checkpoint> ReadCheckpointAsync(CancellationToken token) => Task.FromResult(Checkpoint);
        public Task<DocumentState?> ReadDocumentStateAsync(string table, string id, CancellationToken token) =>
            Task.FromResult(Documents.GetValueOrDefault($"{table}/{id}"));
        public Task WriteDocumentStateAsync(string table, string id, DocumentState state, CancellationToken token)
        {
            if (++stateWrites == FailStateWriteNumber)
                throw new IOException("Simulated partial record state failure.");
            Documents[$"{table}/{id}"] = state;
            return Task.CompletedTask;
        }
        public Task WriteCheckpointAsync(Checkpoint state, CancellationToken token)
        {
            if (FailCheckpoint)
                throw new IOException("Simulated failure after document state committed.");
            Checkpoint = state;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeWriter : IKnowledgeWriter
    {
        public Dictionary<string, KnowledgeDocument> Documents { get; } = [];
        public int Calls { get; private set; }
        public bool FailAfterFirstWrite { get; set; }
        public bool FailAfterFirstDelete { get; set; }
        public Task UpsertAsync(IReadOnlyList<KnowledgeDocument> documents, CancellationToken token)
        {
            Calls++;
            foreach (var document in documents)
            {
                Documents[document.Id] = document;
                if (FailAfterFirstWrite)
                    throw new IOException("Simulated partial Search write failure.");
            }
            return Task.CompletedTask;
        }
        public Task DeleteAsync(IReadOnlyList<string> ids, CancellationToken token)
        {
            Calls++;
            foreach (var id in ids)
            {
                Documents.Remove(id);
                if (FailAfterFirstDelete)
                    throw new IOException("Simulated partial Search delete failure.");
            }
            return Task.CompletedTask;
        }
    }
}
