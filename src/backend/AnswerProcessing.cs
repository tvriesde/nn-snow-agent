using System.Diagnostics;
using Microsoft.Extensions.AI;

namespace Helpdesk.Backend;

public sealed record TokenUsage(long Input, long Output, long Total, long? CachedInput, long? Reasoning);
public sealed record ModelCostEstimate(decimal Amount, string Currency, string Scope, string PricingSource, string PricesAsOf);
public sealed record AnswerProcessing(double ElapsedMilliseconds, string SelectedModelId, string SelectedModelLabel,
    bool ModelInvoked, int ModelCalls, string? ProviderModel, TokenUsage? Tokens, string? TokensUnavailableReason,
    ModelCostEstimate? EstimatedModelCost, string? CostUnavailableReason);

public sealed class ProcessingTracker(ConfiguredModel model)
{
    private readonly long started = Stopwatch.GetTimestamp();
    private readonly List<(UsageDetails? Usage, string? Model)> responses = [];
    public int ModelCalls { get; private set; }
    public void StartModelCall() => ModelCalls++;
    public void RecordResponse(Microsoft.Extensions.AI.ChatResponse response)
    {
        // The function-invoking client mutates response usage while aggregating later rounds.
        var usage = response.Usage;
        responses.Add((usage is null ? null : new UsageDetails
        {
            InputTokenCount = usage.InputTokenCount, OutputTokenCount = usage.OutputTokenCount,
            TotalTokenCount = usage.TotalTokenCount, CachedInputTokenCount = usage.CachedInputTokenCount,
            ReasoningTokenCount = usage.ReasoningTokenCount
        }, response.ModelId));
    }
    public AnswerProcessing Finish()
    {
        TokenUsage? tokens = null;
        ModelCostEstimate? cost = null;
        string? usageGap = null;
        string? costGap = null;
        if (ModelCalls == 0)
        {
            usageGap = "No model was invoked; this answer used a skill, offline mode or an unavailable model capability.";
            costGap = "No model inference was used. Hosting, Search and Azure tool costs are not measured here.";
        }
        else if (responses.Count != ModelCalls || responses.Any(r => !Complete(r.Usage)) ||
            responses.Sum(r => (decimal)r.Usage!.TotalTokenCount!.Value) > long.MaxValue)
        {
            usageGap = "Complete token usage was not returned for every model call; partial totals are withheld.";
            costGap = "A cost estimate requires complete usage for every model call.";
        }
        else
        {
            var usage = responses.Select(r => r.Usage!).ToArray();
            tokens = new(usage.Sum(u => u.InputTokenCount!.Value), usage.Sum(u => u.OutputTokenCount!.Value),
                usage.Sum(u => u.TotalTokenCount!.Value),
                usage.All(u => ValidSubset(u.CachedInputTokenCount, u.InputTokenCount)) ? usage.Sum(u => u.CachedInputTokenCount!.Value) : null,
                usage.All(u => ValidSubset(u.ReasoningTokenCount, u.OutputTokenCount)) ? usage.Sum(u => u.ReasoningTokenCount!.Value) : null);
            if (model.Pricing is not { } pricing)
                costGap = "Verified pricing is not configured for this model.";
            else if (pricing.CachedInputPerMillion is null)
                costGap = "Verified cached-input pricing is not configured for this model.";
            else if (tokens.CachedInput is null)
                costGap = "The cached-input billing breakdown is incomplete.";
            else if (usage.Any(u => u.InputTokenCount > pricing.MaximumInputTokens))
                costGap = "A model call exceeded the configured pricing context tier.";
            else if (pricing.CacheWritePerMillion is not null)
                costGap = "Cache-write billing counts are not available in the verified SDK usage contract; no cost is guessed.";
            else
            {
                var amount = ((tokens.Input - tokens.CachedInput.Value) * pricing.InputPerMillion +
                    tokens.CachedInput.Value * pricing.CachedInputPerMillion.Value + tokens.Output * pricing.OutputPerMillion) / 1_000_000m;
                cost = new(amount, "USD", "Estimated model-token cost only; excludes other Azure services, tax and negotiated discounts.",
                    pricing.Source, pricing.AsOf);
            }
        }
        var providerModels = responses.Select(r => r.Model).Distinct(StringComparer.Ordinal).ToArray();
        return new(Stopwatch.GetElapsedTime(started).TotalMilliseconds, model.Id, model.Label, ModelCalls > 0, ModelCalls,
            providerModels.Length == 1 ? providerModels[0] : null, tokens, usageGap, cost, costGap);
    }
    private static bool Complete(UsageDetails? usage) => usage is { InputTokenCount: >= 0, OutputTokenCount: >= 0, TotalTokenCount: >= 0 } &&
        usage.InputTokenCount <= long.MaxValue - usage.OutputTokenCount &&
        usage.TotalTokenCount == usage.InputTokenCount + usage.OutputTokenCount;
    private static bool ValidSubset(long? count, long? total) => count is >= 0 && count <= total;
}
