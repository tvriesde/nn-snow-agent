using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using Helpdesk.Backend;
using Microsoft.Extensions.AI;
using OpenAI.Responses;
using Xunit;

#pragma warning disable OPENAI001
public sealed class ModelChatOptionsResponsesTests
{
    [Fact]
    public async Task LunaResponsesPreservesToolsStrictEvidenceLowReasoningAndCompleteRoundUsageWithoutStorage()
    {
        using var handler = new ResponsesHandler();
        using var http = new HttpClient(handler);
        var native = new ResponsesClient(new ApiKeyCredential("test-only"), new ResponsesClientOptions
        {
            Endpoint = new Uri("https://example.invalid/openai/v1/"),
            Transport = new HttpClientPipelineTransport(http)
        });
        var ledger = new EvidenceLedger();
        var tracker = new ProcessingTracker(new ConfiguredModel
        { Id = "luna", Label = "Luna", Deployment = "helpdesk-luna", Api = "responses" });
        using var client = new FunctionInvokingChatClient(new EvidenceBoundChatClient(native.AsIChatClient("helpdesk-luna"), ledger, tracker));
        var tools = new List<AITool> { AIFunctionFactory.Create((string query) =>
        {
            ledger.Knowledge["kb-from-tool"] = new("kb-from-tool", "KB0001", "MFA", query, "Identity");
            return "Verified test evidence.";
        }, "SearchKnowledge") };
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Explain MFA.")],
            ModelChatOptions.Create("Ground answers in evidence.", tools, "low", "responses"));
        Assert.Equal(2, handler.Requests.Count);
        for (var index = 0; index < handler.Requests.Count; index++)
        {
            using var request = JsonDocument.Parse(handler.Requests[index]);
            var root = request.RootElement;
            Assert.Equal("helpdesk-luna", root.GetProperty("model").GetString());
            Assert.False(root.GetProperty("store").GetBoolean());
            Assert.Equal("low", root.GetProperty("reasoning").GetProperty("effort").GetString());
            Assert.Equal(2000, root.GetProperty("max_output_tokens").GetInt32());
            var format = root.GetProperty("text").GetProperty("format");
            Assert.True(format.GetProperty("strict").GetBoolean());
            Assert.Equal("json_schema", format.GetProperty("type").GetString());
            var ids = format.GetProperty("schema").GetProperty("properties").GetProperty("knowledgeSourceIds");
            Assert.Equal(index == 0 ? "null" : "array", ids.GetProperty("type").GetString());
            if (index == 1)
            {
                Assert.Contains("kb-from-tool", ids.GetProperty("items").GetProperty("enum").EnumerateArray().Select(v => v.GetString()));
                Assert.Contains(root.GetProperty("input").EnumerateArray(),
                    item => item.TryGetProperty("type", out var type) && type.GetString() == "function_call_output");
            }
            #pragma warning restore OPENAI001
        }
        Assert.Equal(new TokenUsage(300, 60, 360, 30, 15), tracker.Finish().Tokens);
        Assert.Equal(2, tracker.ModelCalls);
        Assert.Equal("https://example.invalid/openai/v1/responses", handler.Endpoint?.AbsoluteUri);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("Responses")]
    public void UnknownApiCannotSilentlySelectAnotherEndpoint(string api)
    {
        var config = ModelSelectionAndUsageTests.Config();
        config["AzureOpenAI:Models:1:Api"] = api;
        Assert.Throws<InvalidOperationException>(() => new ModelCatalog(config));
        Assert.Throws<InvalidOperationException>(() => ModelChatOptions.Create("Test", [], "low", api));
        Assert.Throws<InvalidOperationException>(() => new HelpdeskModelClientFactory().Create("test", new Uri("https://example.invalid"), "test-only", api));
    }

    private sealed class ResponsesHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public Uri? Endpoint { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Endpoint = request.RequestUri;
            Requests.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            var json = Requests.Count == 1 ? """
                {"id":"resp-first","object":"response","created_at":1,"status":"completed","model":"gpt-6-luna",
                "output":[{"id":"fc-test","type":"function_call","call_id":"call-search","name":"SearchKnowledge",
                "arguments":"{\"query\":\"MFA\"}","status":"completed"}],
                "usage":{"input_tokens":100,"output_tokens":20,"total_tokens":120,
                "input_tokens_details":{"cached_tokens":10},"output_tokens_details":{"reasoning_tokens":5}}}
                """ : """
                {"id":"resp-final","object":"response","created_at":1,"status":"completed","model":"gpt-6-luna",
                "output":[{"id":"msg-test","type":"message","role":"assistant","status":"completed",
                "content":[{"type":"output_text","text":"OK","annotations":[]}]}],
                "usage":{"input_tokens":200,"output_tokens":40,"total_tokens":240,
                "input_tokens_details":{"cached_tokens":20},"output_tokens_details":{"reasoning_tokens":10}}}
                """;
            return new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
