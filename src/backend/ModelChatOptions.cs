using Microsoft.Extensions.AI;
using OpenAI.Chat;
using OpenAI.Responses;

namespace Helpdesk.Backend;

public static class ModelChatOptions
{
    public static ChatOptions Create(string instructions, IList<AITool> tools, string? reasoningEffort,
        string api = "chatCompletions")
    {
        if (api is not ("chatCompletions" or "responses"))
            throw new InvalidOperationException("Unsupported Azure OpenAI API.");
        var options = new ChatOptions { Instructions = instructions, Tools = tools, MaxOutputTokens = 2000 };
        if (api == "responses")
        {
            if (!string.IsNullOrWhiteSpace(reasoningEffort) && reasoningEffort is not ("minimal" or "low"))
                throw new InvalidOperationException("AzureOpenAI:ReasoningEffort must be empty, minimal or low.");
#pragma warning disable OPENAI001
            options.RawRepresentationFactory = _ => new CreateResponseOptions
            {
                StoredOutputEnabled = false,
                ReasoningOptions = string.IsNullOrWhiteSpace(reasoningEffort) ? null : new ResponseReasoningOptions
                { ReasoningEffortLevel = new ResponseReasoningEffortLevel(reasoningEffort) }
            };
#pragma warning restore OPENAI001
            return options;
        }
        if (string.IsNullOrWhiteSpace(reasoningEffort))
            return options;
        if (reasoningEffort is not ("minimal" or "low"))
            throw new InvalidOperationException("AzureOpenAI:ReasoningEffort must be empty, minimal or low.");
#pragma warning disable OPENAI001 // The pinned SDK marks its typed reasoning parameter experimental.
        options.RawRepresentationFactory = _ => new ChatCompletionOptions
        {
            ReasoningEffortLevel = new ChatReasoningEffortLevel(reasoningEffort)
        };
#pragma warning restore OPENAI001
        return options;
    }
}
