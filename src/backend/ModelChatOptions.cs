using Microsoft.Extensions.AI;
using OpenAI.Chat;

namespace Helpdesk.Backend;

public static class ModelChatOptions
{
    public static ChatOptions Create(string instructions, IList<AITool> tools, string? reasoningEffort)
    {
        var options = new ChatOptions { Instructions = instructions, Tools = tools, MaxOutputTokens = 2000 };
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
