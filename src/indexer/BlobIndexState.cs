using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Helpdesk.Core;
using Microsoft.Extensions.Logging;

namespace Helpdesk.Indexer;

public sealed class BlobIndexState : IIndexState, IAsyncDisposable
{
    private readonly BlobContainerClient container;
    private readonly BlobClient checkpoint;
    private readonly BlobLeaseClient lease;
    private readonly CancellationTokenSource renewalCancellation;
    private readonly Task renewal;
    public CancellationToken CancellationToken => renewalCancellation.Token;

    private BlobIndexState(BlobContainerClient container, BlobClient checkpoint, BlobLeaseClient lease,
        ILogger logger, CancellationToken cancellationToken)
    {
        this.container = container;
        this.checkpoint = checkpoint;
        this.lease = lease;
        renewalCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        renewal = RenewAsync(logger);
    }

    public static async Task<BlobIndexState?> TryAcquireAsync(BlobContainerClient container,
        ILogger logger, CancellationToken cancellationToken)
    {
        var checkpoint = container.GetBlobClient("checkpoint.json");
        try
        {
            await checkpoint.UploadAsync(BinaryData.FromObjectAsJson(new Checkpoint(0), DeltaIndexer.JsonOptions),
                new BlobUploadOptions { Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All } },
                cancellationToken);
        }
        catch (RequestFailedException ex) when (ex.Status is 409 or 412)
        {
            logger.LogDebug("Checkpoint already exists; retaining its state.");
        }
        var lease = checkpoint.GetBlobLeaseClient();
        try
        {
            await lease.AcquireAsync(TimeSpan.FromSeconds(60), cancellationToken: cancellationToken);
        }
        catch (RequestFailedException ex) when (ex.ErrorCode == "LeaseAlreadyPresent")
        {
            logger.LogWarning("Another indexer owns the checkpoint lease; skipping this scheduled tick.");
            return null;
        }
        return new(container, checkpoint, lease, logger, cancellationToken);
    }

    private async Task RenewAsync(ILogger logger)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(20));
        try
        {
            while (await timer.WaitForNextTickAsync(renewalCancellation.Token))
                await lease.RenewAsync(cancellationToken: renewalCancellation.Token);
        }
        catch (OperationCanceledException) when (renewalCancellation.IsCancellationRequested)
        {
            // Normal function shutdown cancels the renewal timer.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Checkpoint lease renewal failed; cancelling indexing to prevent concurrent writes.");
            await renewalCancellation.CancelAsync();
            throw;
        }
    }

    public async Task<Checkpoint> ReadCheckpointAsync(CancellationToken cancellationToken) =>
        await ReadAsync<Checkpoint>(checkpoint, cancellationToken)
        ?? throw new InvalidDataException("Checkpoint is null.");

    public async Task<DocumentState?> ReadDocumentStateAsync(string table, string sourceId, CancellationToken cancellationToken)
    {
        try
        {
            return await ReadAsync<DocumentState>(DocumentBlob(table, sourceId), cancellationToken)
                ?? throw new InvalidDataException("Document state is null.");
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task WriteDocumentStateAsync(string table, string sourceId, DocumentState state, CancellationToken cancellationToken)
    {
        CancellationToken.ThrowIfCancellationRequested();
        await DocumentBlob(table, sourceId).UploadAsync(
            BinaryData.FromObjectAsJson(state, DeltaIndexer.JsonOptions), overwrite: true, cancellationToken);
    }

    public async Task WriteCheckpointAsync(Checkpoint state, CancellationToken cancellationToken)
    {
        CancellationToken.ThrowIfCancellationRequested();
        await checkpoint.UploadAsync(BinaryData.FromObjectAsJson(state, DeltaIndexer.JsonOptions),
            new BlobUploadOptions { Conditions = new BlobRequestConditions { LeaseId = lease.LeaseId } },
            cancellationToken);
    }

    private BlobClient DocumentBlob(string table, string id) => container.GetBlobClient($"documents/{table}/{id}.json");

    private static async Task<T?> ReadAsync<T>(BlobClient blob, CancellationToken cancellationToken)
    {
        var content = await blob.DownloadContentAsync(cancellationToken);
        return JsonSerializer.Deserialize<T>(content.Value.Content.ToArray(), DeltaIndexer.JsonOptions);
    }

    public async ValueTask DisposeAsync()
    {
        await renewalCancellation.CancelAsync();
        try
        {
            await renewal;
        }
        finally
        {
            try
            {
                await lease.ReleaseAsync(cancellationToken: CancellationToken.None);
            }
            finally
            {
                renewalCancellation.Dispose();
            }
        }
    }
}
