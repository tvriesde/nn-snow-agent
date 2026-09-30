using System.Text.Json;

namespace Helpdesk.Core;

public sealed record SourceManifest(int SchemaVersion, IReadOnlyList<BatchDescriptor> Batches);
public sealed record BatchDescriptor(long Sequence, string Path, string Kind, string? Table, string Sha256, int RecordCount);
public sealed record SourceChange(string Operation, string Table, string SourceId, JsonElement? Record);
public sealed record Checkpoint(long Sequence);
public sealed record DocumentState(long Version, IReadOnlyList<string> DocumentIds);
public sealed record KnowledgeDocument(
    string Id, string SourceId, string Table, string Number, string Title,
    string Content, string Application, string Category, string Visibility,
    DateTimeOffset UpdatedAt, long SourceVersion, int Chunk);
public sealed record IndexingResult(long Sequence, int Batches, int Records, int Upserts, int Deletes);

public interface ISourceReader
{
    Task<SourceManifest> ReadManifestAsync(CancellationToken cancellationToken);
    Task<byte[]> ReadBatchAsync(string path, CancellationToken cancellationToken);
}

public interface IIndexState
{
    Task<Checkpoint> ReadCheckpointAsync(CancellationToken cancellationToken);
    Task<DocumentState?> ReadDocumentStateAsync(string table, string sourceId, CancellationToken cancellationToken);
    Task WriteDocumentStateAsync(string table, string sourceId, DocumentState state, CancellationToken cancellationToken);
    Task WriteCheckpointAsync(Checkpoint checkpoint, CancellationToken cancellationToken);
}

public interface IKnowledgeWriter
{
    Task UpsertAsync(IReadOnlyList<KnowledgeDocument> documents, CancellationToken cancellationToken);
    Task DeleteAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken);
}
