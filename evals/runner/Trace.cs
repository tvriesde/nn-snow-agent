using System.Text.Json;
using Helpdesk.Backend;
using Microsoft.Extensions.AI;

namespace Helpdesk.Evaluation;

public sealed record RetrievalTrace(string Query, IReadOnlyList<KnowledgeSource> Sources, string? Error);
public sealed record ToolTrace(string Name, object? Arguments, string? Error = null);

public sealed class EvaluationTrace
{
    public List<RetrievalTrace> Retrievals { get; } = [];
    public List<ToolTrace> Tools { get; } = [];
    public List<object> Rounds { get; } = [];
    public List<string> Errors { get; } = [];
    public string? RawAnswer { get; set; }
    public bool Succeeded(Helpdesk.Backend.ChatResponse? response) => response is not null && Errors.Count == 0 &&
        response.Warnings.All(warning => warning == HelpdeskAgent.NoKnowledgeSourcesWarning) &&
        response.Processing?.ModelInvoked == true;

    public static object Message(ChatMessage message) => new
    {
        role = message.Role.Value,
        content = message.Contents.Select<AIContent, object>(content => content switch
        {
            TextContent text => new { type = "text", text = text.Text },
            FunctionCallContent call => new { type = "tool_call", id = call.CallId, name = call.Name, arguments = call.Arguments },
            FunctionResultContent result => new { type = "tool_result", id = result.CallId, result = result.Result },
            _ => new { type = content.GetType().Name }
        }).ToArray()
    };
}

public sealed class RecordingKnowledge(IKnowledgeService inner, EvaluationTrace trace) : IKnowledgeService
{
    public async Task<IReadOnlyList<KnowledgeSource>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        try
        {
            var sources = await inner.SearchAsync(query, cancellationToken);
            trace.Retrievals.Add(new(query, sources, null));
            return sources;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            trace.Retrievals.Add(new(query, [], ex.GetType().Name));
            trace.Errors.Add($"SearchKnowledge: {ex.GetType().Name}");
            throw;
        }
    }
    public Task<KnowledgeSource?> GetAsync(string id, CancellationToken cancellationToken) => inner.GetAsync(id, cancellationToken);
}

public sealed class RecordingFactory(EvaluationTrace trace) : IHelpdeskModelClientFactory
{
    public IChatClient Create(string deployment, Uri endpoint, string apiKey, string api = "chatCompletions") =>
        new RecordingClient(new HelpdeskModelClientFactory().Create(deployment, endpoint, apiKey, api), trace);
}

public sealed class RecordingClient(IChatClient inner, EvaluationTrace trace) : DelegatingChatClient(inner)
{
    public override async Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var input = messages.ToArray();
        try
        {
            var response = await base.GetResponseAsync(input, options, cancellationToken);
            foreach (var call in response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>())
                trace.Tools.Add(new(call.Name, call.Arguments));
            trace.RawAnswer = response.Text;
            trace.Rounds.Add(new
            {
                instructions = options?.Instructions,
                tool_definitions = options?.Tools?.OfType<AIFunction>().Select(tool => new
                {
                    name = tool.Name, description = tool.Description, parameters = tool.JsonSchema
                }).ToArray(),
                input = input.Select(EvaluationTrace.Message).ToArray(),
                output = response.Messages.Select(EvaluationTrace.Message).ToArray()
            });
            return response;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            trace.Errors.Add($"Model: {ex.GetType().Name}");
            throw;
        }
    }
}

public sealed class UnavailableAzureTools(EvaluationTrace trace) : IAzureInvestigator, IHealthModelMcp
{
    public Task<AzureEvidence> InvestigateAsync(string alias, string operation, CancellationToken cancellationToken)
    {
        trace.Tools.Add(new("InvestigateAzure", new { alias, operation }, "Live Azure investigation is outside this evaluation."));
        throw new InvalidOperationException("Live Azure investigation is outside this evaluation.");
    }
    public Task<JsonElement> ReadAsync(string? resourceGroup, string? modelName, CancellationToken cancellationToken)
    {
        trace.Tools.Add(new("GetApplicationHealth", new { resourceGroup, modelName }, "Live health is outside this evaluation."));
        throw new AzureMcpQueryException("Live health is outside this evaluation.");
    }
}
