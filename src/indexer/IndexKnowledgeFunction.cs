using Azure.Storage.Blobs;
using Helpdesk.Core;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Helpdesk.Indexer;

public sealed class IndexKnowledgeFunction(BlobServiceClient storage, BlobSourceReader source,
    SearchKnowledgeWriter writer, IConfiguration configuration, ILogger<IndexKnowledgeFunction> logger)
{
    [Function("IndexServiceNowKnowledge")]
    public async Task RunAsync([TimerTrigger("%IndexingSchedule%")] TimerInfo timer,
        CancellationToken cancellationToken)
    {
        if (timer.IsPastDue)
            logger.LogWarning("ServiceNow indexing schedule is past due.");
        var container = storage.GetBlobContainerClient(configuration["State:ContainerName"] ?? "indexer-state");
        await using var state = await BlobIndexState.TryAcquireAsync(container, logger, cancellationToken);
        if (state is null)
            return;
        try
        {
            var result = await new DeltaIndexer(source, state, writer).RunAsync(
                configuration.GetValue("Indexing:MaxBatches", 5), state.CancellationToken);
            logger.LogInformation(
                "Indexing committed sequence {Sequence}; batches {Batches}, records {Records}, upserts {Upserts}, deletes {Deletes}.",
                result.Sequence, result.Batches, result.Records, result.Upserts, result.Deletes);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ServiceNow indexing failed. Uncommitted batches will be replayed.");
            throw;
        }
    }
}
