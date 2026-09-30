using System.Text.Json;
using Microsoft.Extensions.AI;
using AiResponse = Microsoft.Extensions.AI.ChatResponse;

namespace Helpdesk.Backend;

public sealed class EvidenceBoundChatClient(IChatClient innerClient, EvidenceLedger ledger)
    : DelegatingChatClient(innerClient)
{
    public override Task<AiResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        base.GetResponseAsync(messages, BoundOptions(options), cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        base.GetStreamingResponseAsync(messages, BoundOptions(options), cancellationToken);

    private ChatOptions BoundOptions(ChatOptions? options)
    {
        var bound = options?.Clone() ?? new ChatOptions();
        bound.AdditionalProperties = new();
        if (options?.AdditionalProperties is { } properties)
            foreach (var property in properties)
                bound.AdditionalProperties[property.Key] = property.Value;
        bound.AdditionalProperties["strict"] = true;
        var ids = new Dictionary<string, object> { ["type"] = ledger.Knowledge.Count > 0 ? "array" : "null" };
        if (ledger.Knowledge.Count > 0)
            ids["items"] = new { type = "string", @enum = ledger.Knowledge.Keys.Order(StringComparer.Ordinal).ToArray() };
        // Regenerate inside the tool loop so newly retrieved IDs become valid without trusting model citations.
        bound.ResponseFormat = ChatResponseFormat.ForJsonSchema(JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["answer"] = new { type = "string" },
                ["knowledgeSourceIds"] = ids
            },
            required = new[] { "answer", "knowledgeSourceIds" },
            additionalProperties = false
        }), "HelpdeskAnswer");
        return bound;
    }
}
