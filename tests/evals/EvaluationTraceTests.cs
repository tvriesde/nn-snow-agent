using Azure.Core;
using Azure.Identity;
using Helpdesk.Backend;
using Helpdesk.Evaluation;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Xunit;
using AiResponse = Microsoft.Extensions.AI.ChatResponse;

public sealed class EvaluationTraceTests
{
    [Fact]
    public async Task RecordingKnowledgePreservesTheExactRetrievedSources()
    {
        var trace = new EvaluationTrace();
        var source = new KnowledgeSource("real-id", "KB1", "title", "actual excerpt", "app");
        var recording = new RecordingKnowledge(new FixedKnowledge(source), trace);
        var result = await recording.SearchAsync("original query", default);
        Assert.Same(source, Assert.Single(result));
        Assert.Equal("original query", Assert.Single(trace.Retrievals).Query);
        Assert.Same(source, Assert.Single(trace.Retrievals[0].Sources));
    }

    [Fact]
    public async Task RecordingKnowledgeDoesNotHideSearchFailure()
    {
        var trace = new EvaluationTrace();
        var recording = new RecordingKnowledge(new FailingKnowledge(), trace);
        await Assert.ThrowsAsync<InvalidOperationException>(() => recording.SearchAsync("query", default));
        Assert.NotNull(Assert.Single(trace.Retrievals).Error);
        Assert.Single(trace.Errors);
    }

    [Fact]
    public async Task ModelTraceRecordsRawOutputAndToolsWithoutChangingUsageOrInstructions()
    {
        var trace = new EvaluationTrace();
        var response = new AiResponse(new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("call-id", "SearchKnowledge", new Dictionary<string, object?> { ["query"] = "search text" })]))
            { Usage = new() { InputTokenCount = 7, OutputTokenCount = 3, TotalTokenCount = 10 } };
        var fake = new FixedModel(response);
        using var recording = new RecordingClient(fake, trace);
        var options = new ChatOptions { Instructions = "actual instructions" };
        var result = await recording.GetResponseAsync([new(ChatRole.User, "question")], options);
        Assert.Same(response, result);
        Assert.Same(options, fake.Options);
        Assert.Equal(10, result.Usage!.TotalTokenCount);
        Assert.Equal("SearchKnowledge", Assert.Single(trace.Tools).Name);
        Assert.Single(trace.Rounds);
    }

    [Fact]
    public async Task LiveAzureToolsAreExplicitlyUnavailableAndRecorded()
    {
        var trace = new EvaluationTrace();
        var tools = new UnavailableAzureTools(trace);
        await Assert.ThrowsAsync<InvalidOperationException>(() => tools.InvestigateAsync("backend", "metrics", default));
        await Assert.ThrowsAsync<AzureMcpQueryException>(() => tools.ReadAsync(null, null, default));
        Assert.Equal(2, trace.Tools.Count);
        Assert.All(trace.Tools, tool => Assert.NotNull(tool.Error));
    }

    [Fact]
    public async Task LocalKnowledgeSearchUsesTheInjectedCredential()
    {
        var credential = new RejectingCredential();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Search:Endpoint"] = "https://search.example.search.windows.net" }).Build();
        var service = new KnowledgeService(config, credential);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<AuthenticationFailedException>(() => service.SearchAsync("query", timeout.Token));
        Assert.True(credential.Called);
    }

    [Fact]
    public void EmptySuccessfulSearchIsNotAnOperationalFailure()
    {
        var tracker = new ProcessingTracker(new() { Id = "test", Label = "Test", Deployment = "test" });
        tracker.StartModelCall();
        tracker.RecordResponse(new AiResponse(new ChatMessage(ChatRole.Assistant, "answer"))
            { Usage = new() { InputTokenCount = 1, OutputTokenCount = 1, TotalTokenCount = 2 } });
        var response = new Helpdesk.Backend.ChatResponse("id", "No relevant sources are available.", [], [],
            [HelpdeskAgent.NoKnowledgeSourcesWarning], tracker.Finish());
        var trace = new EvaluationTrace();
        Assert.True(trace.Succeeded(response));
        Assert.False(trace.Succeeded(response with { Warnings = ["Knowledge search is unavailable."] }));
        trace.Errors.Add("Actual search failure");
        Assert.False(trace.Succeeded(response));
    }

    private sealed class FixedKnowledge(KnowledgeSource source) : IKnowledgeService
    {
        public Task<IReadOnlyList<KnowledgeSource>> SearchAsync(string query, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<KnowledgeSource>>([source]);
        public Task<KnowledgeSource?> GetAsync(string id, CancellationToken cancellationToken) =>
            Task.FromResult<KnowledgeSource?>(source);
    }
    private sealed class FailingKnowledge : IKnowledgeService
    {
        public Task<IReadOnlyList<KnowledgeSource>> SearchAsync(string query, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Search is unavailable.");
        public Task<KnowledgeSource?> GetAsync(string id, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
    private sealed class RejectingCredential : TokenCredential
    {
        public bool Called { get; private set; }
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        { Called = true; throw new AuthenticationFailedException("Expected test credential failure."); }
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        { Called = true; throw new AuthenticationFailedException("Expected test credential failure."); }
    }
    private sealed class FixedModel(AiResponse response) : IChatClient
    {
        public ChatOptions? Options { get; private set; }
        public Task<AiResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        { Options = options; return Task.FromResult(response); }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
