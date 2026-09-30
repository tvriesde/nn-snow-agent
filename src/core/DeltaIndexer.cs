using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Helpdesk.Core;

public sealed class DeltaIndexer(ISourceReader source, IIndexState state, IKnowledgeWriter writer)
{
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web);

    public async Task<IndexingResult> RunAsync(int maxBatches, CancellationToken cancellationToken = default)
    {
        if (maxBatches is < 1 or > 50)
            throw new ArgumentOutOfRangeException(nameof(maxBatches));
        var manifest = await source.ReadManifestAsync(cancellationToken);
        ValidateManifest(manifest);
        var checkpoint = await state.ReadCheckpointAsync(cancellationToken);
        if (checkpoint.Sequence < 0 || checkpoint.Sequence > manifest.Batches.Count)
            throw new InvalidDataException("Checkpoint is outside the committed manifest.");
        var batches = 0;
        var records = 0;
        var upserts = 0;
        var deletes = 0;
        foreach (var batch in manifest.Batches.Where(b => b.Sequence > checkpoint.Sequence).Take(maxBatches))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = await source.ReadBatchAsync(batch.Path, cancellationToken);
            var actualHash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            if (!string.Equals(actualHash, batch.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Hash mismatch for committed batch {batch.Sequence}.");
            var changes = ParseBatch(batch, bytes);
            var pendingDocuments = new List<KnowledgeDocument>();
            var pendingDeletes = new List<string>();
            var pendingStates = new List<(string Table, string SourceId, DocumentState State)>();
            foreach (var change in changes)
            {
                var prior = await state.ReadDocumentStateAsync(change.Table, change.SourceId, cancellationToken);
                if (prior?.Version > batch.Sequence)
                    throw new InvalidDataException("Document state is ahead of the batch being processed.");
                if (prior?.Version == batch.Sequence)
                    continue;
                var documents = change.Operation == "delete" ? [] :
                    ServiceNowProjection.Project(change.Table, change.Record!.Value, batch.Sequence);
                var currentIds = documents.Select(d => d.Id).ToArray();
                var obsolete = (prior?.DocumentIds ?? []).Except(currentIds).ToArray();
                pendingDocuments.AddRange(documents);
                pendingDeletes.AddRange(obsolete);
                pendingStates.Add((change.Table, change.SourceId, new(batch.Sequence, currentIds)));
            }
            if (pendingDocuments.Count > 0)
                await writer.UpsertAsync(pendingDocuments, cancellationToken);
            if (pendingDeletes.Count > 0)
                await writer.DeleteAsync(pendingDeletes, cancellationToken);
            // Persist record versions only after all Search actions succeed, so failed batches remain replayable.
            foreach (var pending in pendingStates)
                await state.WriteDocumentStateAsync(pending.Table, pending.SourceId, pending.State, cancellationToken);
            records += pendingStates.Count;
            upserts += pendingDocuments.Count;
            deletes += pendingDeletes.Count;
            checkpoint = new(batch.Sequence);
            await state.WriteCheckpointAsync(checkpoint, cancellationToken);
            batches++;
        }
        return new(checkpoint.Sequence, batches, records, upserts, deletes);
    }

    public static void ValidateManifest(SourceManifest manifest)
    {
        if (manifest.SchemaVersion != 1 || manifest.Batches is null)
            throw new InvalidDataException("Unsupported or missing source manifest.");
        var paths = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < manifest.Batches.Count; i++)
        {
            var batch = manifest.Batches[i];
            if (batch.Sequence != i + 1L || batch.RecordCount is < 0 or > 1000 ||
                string.IsNullOrEmpty(batch.Path) ||
                !Regex.IsMatch(batch.Path, @"^(baseline|changes)/[a-zA-Z0-9_-]+\.json$") ||
                !paths.Add(batch.Path) || string.IsNullOrEmpty(batch.Sha256) ||
                !Regex.IsMatch(batch.Sha256, "^[a-fA-F0-9]{64}$") ||
                batch.Kind is not ("snapshot" or "delta") ||
                (batch.Kind == "snapshot" && (batch.Table is null || !ServiceNowProjection.Tables.Contains(batch.Table))))
                throw new InvalidDataException($"Invalid manifest descriptor at sequence {i + 1}.");
        }
    }

    private static List<SourceChange> ParseBatch(BatchDescriptor batch, byte[] bytes)
    {
        using var payload = JsonDocument.Parse(bytes);
        var root = payload.RootElement;
        var changes = new List<SourceChange>();
        if (batch.Kind == "snapshot")
        {
            foreach (var record in root.GetProperty("result").EnumerateArray())
                changes.Add(new("upsert", batch.Table!, ServiceNowProjection.SourceId(record), record.Clone()));
        }
        else
        {
            foreach (var item in root.GetProperty("changes").EnumerateArray())
            {
                var operation = item.GetProperty("operation").GetString();
                var table = item.GetProperty("table").GetString();
                if (operation is not ("upsert" or "delete") || table is null ||
                    !ServiceNowProjection.Tables.Contains(table))
                    throw new InvalidDataException("Unknown delta operation or table.");
                if (operation == "upsert")
                {
                    var record = item.GetProperty("record");
                    changes.Add(new(operation, table, ServiceNowProjection.SourceId(record), record.Clone()));
                }
                else
                {
                    var id = item.GetProperty("sys_id").GetString();
                    if (id is null || !Regex.IsMatch(id, "^[a-fA-F0-9]{32}$"))
                        throw new InvalidDataException("Invalid tombstone sys_id.");
                    changes.Add(new(operation, table, id.ToLowerInvariant(), null));
                }
            }
        }
        if (changes.Count != batch.RecordCount ||
            changes.Select(c => $"{c.Table}/{c.SourceId}").Distinct().Count() != changes.Count)
            throw new InvalidDataException("Batch count mismatch or duplicate record keys.");
        return changes;
    }
}
