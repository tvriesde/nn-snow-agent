using Helpdesk.Backend;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Xunit;
using AiResponse = Microsoft.Extensions.AI.ChatResponse;

public sealed class ModelSelectionAndUsageTests
{
    public static IConfiguration Config() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["AzureOpenAI:Endpoint"] = "https://model.example", ["AzureOpenAI:ApiKey"] = "test-only",
        ["AzureOpenAI:DefaultModelId"] = "gpt-5-nano",
        ["AzureOpenAI:Models:0:Id"] = "gpt-5-nano", ["AzureOpenAI:Models:0:Label"] = "GPT-5 nano",
        ["AzureOpenAI:Models:0:Deployment"] = "helpdesk-mini", ["AzureOpenAI:Models:0:ReasoningEffort"] = "low",
        ["AzureOpenAI:Models:1:Id"] = "gpt-6-luna", ["AzureOpenAI:Models:1:Label"] = "GPT-6 luna",
        ["AzureOpenAI:Models:1:Deployment"] = "helpdesk-luna", ["AzureOpenAI:Models:1:ReasoningEffort"] = "low",
        ["AzureOpenAI:Models:1:Api"] = "responses",
        ["Azure:HealthApplication"] = "employee-it-helpdesk"
    }).Build();
    [Fact]
    public void PublicCatalogContainsNoEndpointKeysOrDeploymentAuthority()
    {
        var catalog = new ModelCatalog(Config());
        var result = catalog.PublicCatalog();
        Assert.Equal("gpt-5-nano", result.DefaultModelId);
        Assert.Equal(["gpt-5-nano", "gpt-6-luna"], result.Models.Select(m => m.Id));
        Assert.True(catalog.TryResolve(null, out var defaultModel));
        Assert.Equal("helpdesk-mini", defaultModel.Deployment);
        Assert.True(catalog.TryResolve("gpt-6-luna", out var luna));
        Assert.Equal("helpdesk-luna", luna.Deployment);
        foreach (var forbidden in new[] { "", "helpdesk-luna", "https://other.example", "GPT-6-LUNA", "unknown" })
            Assert.False(catalog.TryResolve(forbidden, out _));
    }
    [Theory]
    [InlineData("gpt-5-nano", "helpdesk-mini")]
    [InlineData("gpt-6-luna", "helpdesk-luna")]
    public async Task AgentUsesTheSelectedDeploymentAndReportsRealCompleteUsage(string id, string deployment)
    {
        var factory = new RecordingFactory();
        var config = Config();
        var agent = new HelpdeskAgent(config, new EmptyKnowledge(), new ForbiddenInvestigator(),
            ApplicationHealthSkillTests.Skill(new()), factory, new ModelCatalog(config));
        var result = await agent.RunAsync(new("id", "owner", DateTimeOffset.UtcNow), "Explain MFA recovery.", false, default, id);
        Assert.Equal(deployment, factory.Deployment);
        Assert.Equal(id == "gpt-6-luna" ? "responses" : "chatCompletions", factory.Api);
        var processing = Assert.IsType<AnswerProcessing>(result.Processing);
        Assert.True(processing.ModelInvoked);
        Assert.Equal(id, processing.SelectedModelId);
        Assert.Equal(1, processing.ModelCalls);
        Assert.Equal("reported-provider-model", processing.ProviderModel);
        Assert.True(processing.ElapsedMilliseconds >= 0);
        Assert.Equal(new TokenUsage(100, 25, 125, 10, 5), processing.Tokens);
        Assert.Null(processing.EstimatedModelCost);
        Assert.Contains("pricing", processing.CostUnavailableReason);
    }
    [Fact]
    public async Task InvalidModelSelectionDoesNotInvokeDependenciesOrSilentlyFallBack()
    {
        var factory = new RecordingFactory();
        var config = Config();
        var agent = new HelpdeskAgent(config, new EmptyKnowledge(), new ForbiddenInvestigator(),
            ApplicationHealthSkillTests.Skill(new()), factory, new ModelCatalog(config));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            agent.RunAsync(new("id", "owner", DateTimeOffset.UtcNow), "Question", false, default, "not-allowed"));
        Assert.Null(factory.Deployment);
    }
    [Fact]
    public async Task HealthFastPathReportsSelectedModelButDoesNotPretendToUseIt()
    {
        var factory = new RecordingFactory();
        var config = Config();
        var agent = new HelpdeskAgent(config, new EmptyKnowledge(), new ForbiddenInvestigator(),
            ApplicationHealthSkillTests.Skill(new()), factory, new ModelCatalog(config));
        var result = await agent.RunAsync(new("id", "owner", DateTimeOffset.UtcNow), "Is this helpdesk healthy?", false, default, "gpt-6-luna");
        Assert.Equal("gpt-6-luna", result.Processing!.SelectedModelId);
        Assert.False(result.Processing.ModelInvoked);
        Assert.Equal(0, result.Processing.ModelCalls);
        Assert.Null(result.Processing.Tokens);
        Assert.Null(result.Processing.EstimatedModelCost);
        Assert.Null(factory.Deployment);
    }
    [Fact]
    public void UsageAndCostsIncludeAllRoundsWithoutDoubleCountingCachedOrReasoningTokens()
    {
        var tracker = new ProcessingTracker(PricedModel());
        Round(tracker, 100, 20, 10, 5);
        Round(tracker, 200, 40, 20, 10);
        var result = tracker.Finish();
        Assert.Equal(2, result.ModelCalls);
        Assert.Equal(new TokenUsage(300, 60, 360, 30, 15), result.Tokens);
        Assert.Equal((270 * 0.055m + 30 * 0.0055m + 60 * 0.44m) / 1_000_000m, result.EstimatedModelCost!.Amount);
        Assert.Equal("USD", result.EstimatedModelCost.Currency);
        Assert.Contains("excludes", result.EstimatedModelCost.Scope);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingUsageOrFailedRoundWithholdsAllTotalsAndCosts(bool failed)
    {
        var tracker = new ProcessingTracker(PricedModel());
        Round(tracker, 100, 20, 10, 5);
        tracker.StartModelCall();
        if (!failed) tracker.RecordResponse(new AiResponse(new ChatMessage(ChatRole.Assistant, "no usage")));
        var result = tracker.Finish();
        Assert.Null(result.Tokens);
        Assert.Null(result.EstimatedModelCost);
        Assert.Contains("partial totals", result.TokensUnavailableReason);
    }
    [Fact]
    public void CacheReasoningAndPriceGapsAreNeverDefaultedToZero()
    {
        var tracker = new ProcessingTracker(PricedModel());
        Round(tracker, 100, 20, null, null);
        var result = tracker.Finish();
        Assert.NotNull(result.Tokens);
        Assert.Null(result.Tokens.CachedInput);
        Assert.Null(result.Tokens.Reasoning);
        Assert.Null(result.EstimatedModelCost);
        Assert.Contains("cached-input", result.CostUnavailableReason);
    }
    [Fact]
    public void LunaCacheWritesAndOutOfTierPricingCannotProduceMisleadingCost()
    {
        var luna = PricedModel();
        luna.Pricing!.CacheWritePerMillion = 0.15m;
        var tracker = new ProcessingTracker(luna);
        Round(tracker, 100, 20, 0, 0);
        Assert.Contains("Cache-write", tracker.Finish().CostUnavailableReason);
        var nano = PricedModel();
        nano.Pricing!.MaximumInputTokens = 50;
        tracker = new(nano);
        Round(tracker, 100, 20, 0, 0);
        Assert.Null(tracker.Finish().EstimatedModelCost);
        Assert.Contains("context tier", tracker.Finish().CostUnavailableReason);
    }
    [Fact]
    public void InconsistentProviderTotalsAreNotReportedAsCompleteUsage()
    {
        var tracker = new ProcessingTracker(PricedModel());
        tracker.StartModelCall();
        tracker.RecordResponse(new AiResponse(new ChatMessage(ChatRole.Assistant, "test"))
        { Usage = new() { InputTokenCount = 100, OutputTokenCount = 20, TotalTokenCount = 100 } });
        Assert.Null(tracker.Finish().Tokens);
    }
    [Fact]
    public void MissingCachedInputRateCannotBeAssumedFree()
    {
        var config = Config();
        config["AzureOpenAI:Models:0:Pricing:InputPerMillion"] = "0.055";
        config["AzureOpenAI:Models:0:Pricing:OutputPerMillion"] = "0.44";
        config["AzureOpenAI:Models:0:Pricing:MaximumInputTokens"] = "20000";
        config["AzureOpenAI:Models:0:Pricing:Source"] = "https://prices.azure.com/api/retail/prices";
        config["AzureOpenAI:Models:0:Pricing:AsOf"] = "2026-10-02";
        Assert.Throws<InvalidOperationException>(() => new ModelCatalog(config));
    }
    [Fact]
    public void UnrepresentableAggregateIsWithheldInsteadOfCrashingOrWrapping()
    {
        var tracker = new ProcessingTracker(PricedModel());
        Round(tracker, long.MaxValue, 0, 0, 0);
        Round(tracker, 1, 0, 0, 0);
        Assert.Null(tracker.Finish().Tokens);
        Assert.Null(tracker.Finish().EstimatedModelCost);
    }
    private static ConfiguredModel PricedModel() => new()
    {
        Id = "nano", Label = "Nano", Deployment = "configured",
        Pricing = new() { InputPerMillion = 0.055m, CachedInputPerMillion = 0.0055m, OutputPerMillion = 0.44m,
            MaximumInputTokens = 20000, Source = "https://prices.azure.com/api/retail/prices", AsOf = "2026-10-02" }
    };
    private static void Round(ProcessingTracker tracker, long input, long output, long? cached, long? reasoning)
    {
        tracker.StartModelCall();
        tracker.RecordResponse(new AiResponse(new ChatMessage(ChatRole.Assistant, "test"))
        {
            ModelId = "actual-model", Usage = new() { InputTokenCount = input, OutputTokenCount = output,
                TotalTokenCount = input + output, CachedInputTokenCount = cached, ReasoningTokenCount = reasoning }
        });
    }
    private sealed class EmptyKnowledge : IKnowledgeService
    {
        public Task<IReadOnlyList<KnowledgeSource>> SearchAsync(string query, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<KnowledgeSource>>([]);
        public Task<KnowledgeSource?> GetAsync(string id, CancellationToken cancellationToken) => Task.FromResult<KnowledgeSource?>(null);
    }
    private sealed class ForbiddenInvestigator : IAzureInvestigator
    {
        public Task<AzureEvidence> InvestigateAsync(string alias, string operation, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
    private sealed class RecordingFactory : IHelpdeskModelClientFactory
    {
        public string? Deployment { get; private set; }
        public string? Api { get; private set; }
        public IChatClient Create(string deployment, Uri endpoint, string apiKey, string api = "chatCompletions")
        { Deployment = deployment; Api = api; return new FakeModel(); }
    }
    private sealed class FakeModel : IChatClient
    {
        public Task<AiResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AiResponse(new ChatMessage(ChatRole.Assistant, """{"answer":"Safe test advice","knowledgeSourceIds":null}"""))
            { ModelId = "reported-provider-model", Usage = new() { InputTokenCount = 100, OutputTokenCount = 25, TotalTokenCount = 125,
                CachedInputTokenCount = 10, ReasoningTokenCount = 5 } });
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
