using Azure.Identity;
using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;

namespace Helpdesk.Backend;

public interface IKnowledgeService
{
    Task<IReadOnlyList<KnowledgeSource>> SearchAsync(string query, CancellationToken cancellationToken);
    Task<KnowledgeSource?> GetAsync(string id, CancellationToken cancellationToken);
}
public sealed class KnowledgeService : IKnowledgeService
{
    private readonly SearchClient? client;
    public KnowledgeService(IConfiguration config)
    {
        if (Uri.TryCreate(config["Search:Endpoint"], UriKind.Absolute, out var endpoint) && endpoint.Scheme == "https")
            client = new SearchClient(endpoint, config["Search:IndexName"] ?? "servicenow-knowledge",
                new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned));
    }
    public async Task<IReadOnlyList<KnowledgeSource>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        if (client is null) throw new InvalidOperationException("Knowledge search is not configured.");
        var options = new SearchOptions { Filter = "visibility eq 'employee'", Size = 5, QueryType = SearchQueryType.Simple };
        options.Select.Add("id"); options.Select.Add("number"); options.Select.Add("title");
        options.Select.Add("content"); options.Select.Add("application");
        var response = await client.SearchAsync<SearchDocument>(query[..Math.Min(query.Length, 500)], options, cancellationToken);
        var sources = new List<KnowledgeSource>();
        await foreach (var result in response.Value.GetResultsAsync()) sources.Add(Project(result.Document));
        return sources;
    }
    public async Task<KnowledgeSource?> GetAsync(string id, CancellationToken cancellationToken)
    {
        if (client is null) throw new InvalidOperationException("Knowledge search is not configured.");
        try
        {
            var response = await client.GetDocumentAsync<SearchDocument>(id, cancellationToken: cancellationToken);
            return response.Value.TryGetValue("visibility", out var visibility) && visibility?.ToString() == "employee"
                ? Project(response.Value) : null;
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { return null; }
    }
    private static KnowledgeSource Project(SearchDocument document)
    {
        string Read(string key) => document.TryGetValue(key, out var value) ? value?.ToString() ?? "" : "";
        var content = Read("content");
        return new(Read("id"), Read("number"), Read("title"), content[..Math.Min(content.Length, 1800)], Read("application"));
    }
}
