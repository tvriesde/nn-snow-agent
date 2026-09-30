using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using Helpdesk.Core;

namespace Helpdesk.Indexer;

public sealed class SearchKnowledgeWriter(SearchClient search) : IKnowledgeWriter
{
    public Task UpsertAsync(IReadOnlyList<KnowledgeDocument> documents, CancellationToken cancellationToken) =>
        SubmitAsync(documents.Select(doc => IndexDocumentsAction.Upload(new SearchDocument
        {
            ["id"] = doc.Id, ["sourceId"] = doc.SourceId, ["table"] = doc.Table,
            ["number"] = doc.Number, ["title"] = doc.Title, ["content"] = doc.Content,
            ["application"] = doc.Application, ["category"] = doc.Category,
            ["visibility"] = doc.Visibility, ["updatedAt"] = doc.UpdatedAt,
            ["sourceVersion"] = doc.SourceVersion, ["chunk"] = doc.Chunk
        })).ToArray(), cancellationToken);

    public Task DeleteAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken) =>
        SubmitAsync(ids.Select(id => IndexDocumentsAction.Delete(new SearchDocument { ["id"] = id })).ToArray(),
            cancellationToken);

    private async Task SubmitAsync(IReadOnlyList<IndexDocumentsAction<SearchDocument>> actions,
        CancellationToken cancellationToken)
    {
        foreach (var page in actions.Chunk(100))
        {
            var pending = page;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var response = await search.IndexDocumentsAsync(
                    IndexDocumentsBatch.Create(pending),
                    new IndexDocumentsOptions { ThrowOnAnyError = false }, cancellationToken);
                var failed = response.Value.Results.Where(result => !result.Succeeded).ToArray();
                if (failed.Length == 0)
                    break;
                if (attempt == 2 || failed.Any(result => result.Status is not (408 or 409 or 422 or 429 or 500 or 502 or 503 or 504)))
                    throw new InvalidOperationException(
                        $"Search failed {failed.Length} actions; checkpoint not advanced. Statuses: " +
                        string.Join(", ", failed.Select(result => result.Status)));
                var failedIds = failed.Select(result => result.Key).ToHashSet(StringComparer.Ordinal);
                pending = pending.Where(action => failedIds.Contains((string)action.Document["id"])).ToArray();
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt + 1)), cancellationToken);
            }
        }
    }
}
