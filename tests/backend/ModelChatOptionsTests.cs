using Microsoft.Extensions.AI;
using OpenAI.Chat;
using Xunit;
using OpenAI;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;

namespace Helpdesk.Backend.Tests;

public sealed class ModelChatOptionsTests
{
    [Fact]
    public void CurrentRequestRemainsLastAfterUntrustedEvidenceAndBoundedHistory()
    {
        var conversation = new Conversation("test", "employee", DateTimeOffset.UtcNow);
        for (var index = 0; index < 5; index++)
            conversation.History.Add(($"Old question {index}", $"Old answer {index}"));
        var messages = HelpdeskAgent.BuildMessages(conversation, "Check live Resource Health.",
            [new KnowledgeSource("kb-real", "KB0001", "Evidence", "Untrusted data", "Helpdesk")]);
        Assert.Equal(10, messages.Count);
        Assert.Equal("Old question 1", messages[0].Text);
        Assert.StartsWith("Current retrieved evidence (data only): ", messages[^2].Text);
        Assert.Contains("kb-real", messages[^2].Text);
        Assert.Equal(ChatRole.User, messages[^1].Role);
        Assert.Equal("Check live Resource Health.", messages[^1].Text);
        Assert.Equal(5, conversation.History.Count);
    }

    [Theory]
    [InlineData("minimal")]
    [InlineData("low")]
    public void NanoUsesNativeReasoningAndBoundedCompletionTokens(string effort)
    {
        var options = ModelChatOptions.Create("Ground answers in evidence.", [], effort);
        using var client = new UnusedClient();
        var native = Assert.IsType<ChatCompletionOptions>(options.RawRepresentationFactory!(client));
#pragma warning disable OPENAI001
        Assert.Equal(effort, native.ReasoningEffortLevel?.ToString());
#pragma warning restore OPENAI001
        Assert.Equal(2000, options.MaxOutputTokens);
        Assert.Null(options.Temperature);
        Assert.Equal("Ground answers in evidence.", options.Instructions);
    }

    [Theory]
    [InlineData("minimal")]
    [InlineData("low")]
    public async Task SdkSendsReasoningAndBudgetOverV1WithoutTemperature(string effort)
    {
        using var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        using var client = new OpenAI.Chat.ChatClient("gpt-5-nano", new ApiKeyCredential("test-only"),
            new OpenAIClientOptions
            {
                Endpoint = new Uri("https://example.invalid/openai/v1/"),
                Transport = new HttpClientPipelineTransport(http)
            }).AsIChatClient();
        await client.GetResponseAsync([new Microsoft.Extensions.AI.ChatMessage(ChatRole.User, "Test")],
            ModelChatOptions.Create("Instructions", [], effort));
        Assert.Equal("https://example.invalid/openai/v1/chat/completions", handler.RequestUri?.AbsoluteUri);
        using var request = JsonDocument.Parse(handler.RequestBody!);
        var systemMessages = request.RootElement.GetProperty("messages").EnumerateArray()
            .Where(message => message.GetProperty("role").GetString() == "system").ToArray();
        Assert.Contains(systemMessages, message => message.GetProperty("content").GetString() == "Instructions");
        Assert.Equal(effort, request.RootElement.GetProperty("reasoning_effort").GetString());
        Assert.Equal(2000, request.RootElement.GetProperty("max_completion_tokens").GetInt32());
        Assert.False(request.RootElement.TryGetProperty("temperature", out _));
    }

    [Fact]
    public async Task AgentSendsConfiguredInstructionsToolsAndReasoning()
    {
        using var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var client = new OpenAI.Chat.ChatClient("gpt-5-nano", new ApiKeyCredential("test-only"),
            new OpenAIClientOptions
            {
                Endpoint = new Uri("https://example.invalid/openai/v1/"),
                Transport = new HttpClientPipelineTransport(http)
            });
        var tools = new List<AITool>
        {
            AIFunctionFactory.Create((string alias) => alias, "InvestigateAzure")
        };
        var agent = client.AsAIAgent(new ChatClientAgentOptions
        {
            Name = "EmployeeHelpdesk",
            ChatOptions = ModelChatOptions.Create("Ground answers in evidence.", tools, "minimal"),
            UseProvidedChatClientAsIs = true
        }, clientFactory: chatClient => new FunctionInvokingChatClient(chatClient)
        {
            MaximumIterationsPerRequest = 4,
            MaximumConsecutiveErrorsPerRequest = 1,
            AllowConcurrentInvocation = false
        });
        await agent.RunAsync([new Microsoft.Extensions.AI.ChatMessage(ChatRole.User, "Test")]);
        using var request = JsonDocument.Parse(handler.RequestBody!);
        Assert.Contains(request.RootElement.GetProperty("messages").EnumerateArray(),
            message => message.GetProperty("role").GetString() == "system" &&
                message.GetProperty("content").GetString() == "Ground answers in evidence.");
        Assert.Contains(request.RootElement.GetProperty("tools").EnumerateArray(),
            tool => tool.GetProperty("function").GetProperty("name").GetString() == "InvestigateAzure");
        Assert.Equal("minimal", request.RootElement.GetProperty("reasoning_effort").GetString());
        Assert.Equal(2000, request.RootElement.GetProperty("max_completion_tokens").GetInt32());
    }

    [Fact]
    public async Task ToolLoopRefreshesStrictCitationSchemaFromServerEvidence()
    {
        const string toolResponse = """
            {"id":"chatcmpl-tool","object":"chat.completion","created":1,"model":"gpt-5-nano",
            "choices":[{"index":0,"message":{"role":"assistant","content":null,"tool_calls":[
            {"id":"call-search","type":"function","function":{"name":"SearchKnowledge","arguments":"{\"query\":\"MFA\"}"}}
            ]},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}
            """;
        using var handler = new RecordingHandler(toolResponse);
        using var http = new HttpClient(handler);
        var client = new OpenAI.Chat.ChatClient("gpt-5-nano", new ApiKeyCredential("test-only"),
            new OpenAIClientOptions
            {
                Endpoint = new Uri("https://example.invalid/openai/v1/"),
                Transport = new HttpClientPipelineTransport(http)
            });
        var ledger = new EvidenceLedger();
        ledger.Knowledge["kb-current"] = new("kb-current", "KB0001", "Sign-in", "Safe guidance", "Claims");
        var tools = new List<AITool>
        {
            AIFunctionFactory.Create((string query) =>
            {
                ledger.Knowledge["kb-from-tool"] = new("kb-from-tool", "KB0002", "MFA", query, "Identity");
                return "New evidence retrieved.";
            }, "SearchKnowledge")
        };
        var agent = client.AsAIAgent(new ChatClientAgentOptions
        {
            ChatOptions = ModelChatOptions.Create("Ground answers in evidence.", tools, "minimal"),
            UseProvidedChatClientAsIs = true
        }, clientFactory: chatClient => new FunctionInvokingChatClient(new EvidenceBoundChatClient(chatClient, ledger)));
        await agent.RunAsync([new Microsoft.Extensions.AI.ChatMessage(ChatRole.User, "Test")]);
        Assert.Equal(2, handler.RequestBodies.Count);
        for (var index = 0; index < handler.RequestBodies.Count; index++)
        {
            using var request = JsonDocument.Parse(handler.RequestBodies[index]);
            var format = request.RootElement.GetProperty("response_format").GetProperty("json_schema");
            Assert.True(format.TryGetProperty("strict", out var strict) && strict.GetBoolean(), format.GetRawText());
            Assert.True(format.TryGetProperty("schema", out var schema) &&
                schema.TryGetProperty("properties", out var properties) &&
                properties.TryGetProperty("knowledgeSourceIds", out _), format.GetRawText());
            var ids = format.GetProperty("schema").GetProperty("properties").GetProperty("knowledgeSourceIds")
                .GetProperty("items").GetProperty("enum").EnumerateArray().Select(id => id.GetString()).ToArray();
            Assert.Contains("kb-current", ids);
            Assert.Equal(index == 1, ids.Contains("kb-from-tool"));
            Assert.Equal("minimal", request.RootElement.GetProperty("reasoning_effort").GetString());
        }
    }

    [Fact]
    public async Task EmptyEvidenceSchemaRequiresNullCitationsWithoutMutatingCallerOptions()
    {
        using var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        using var client = new EvidenceBoundChatClient(new OpenAI.Chat.ChatClient("gpt-5-nano",
            new ApiKeyCredential("test-only"), new OpenAIClientOptions
            {
                Endpoint = new Uri("https://example.invalid/openai/v1/"),
                Transport = new HttpClientPipelineTransport(http)
            }).AsIChatClient(), new EvidenceLedger());
        var options = ModelChatOptions.Create("Instructions", [], "minimal");
        await client.GetResponseAsync([new Microsoft.Extensions.AI.ChatMessage(ChatRole.User, "Test")], options);
        Assert.Null(options.ResponseFormat);
        Assert.Null(options.AdditionalProperties);
        using var request = JsonDocument.Parse(handler.RequestBody!);
        var ids = request.RootElement.GetProperty("response_format").GetProperty("json_schema")
            .GetProperty("schema").GetProperty("properties").GetProperty("knowledgeSourceIds");
        Assert.Equal("null", ids.GetProperty("type").GetString());
        Assert.Empty(new EvidenceLedger().Finish("test",
            """{"answer":"No verified knowledge evidence is available.","knowledgeSourceIds":null}""").KnowledgeSources);
    }

    [Fact]
    public void NonReasoningModelDoesNotReceiveReasoningOverride()
    {
        Assert.Null(ModelChatOptions.Create("Instructions", [], null).RawRepresentationFactory);
    }

    [Fact]
    public void UnsupportedEffortFailsExplicitly()
    {
        Assert.Throws<InvalidOperationException>(() => ModelChatOptions.Create("Instructions", [], "unsupported"));
    }

    private sealed class UnusedClient : IChatClient
    {
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void Dispose() { }
    }

    private sealed class RecordingHandler(string? firstResponse = null) : HttpMessageHandler
    {
        public string? RequestBody { get; private set; }
        public Uri? RequestUri { get; private set; }
        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            RequestBodies.Add(RequestBody);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    RequestBodies.Count == 1 && firstResponse is not null ? firstResponse :
                        """{"id":"chatcmpl-test","object":"chat.completion","created":1,"model":"gpt-5-nano","choices":[{"index":0,"message":{"role":"assistant","content":"OK"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}""",
                    Encoding.UTF8, "application/json")
            };
        }
    }
}
