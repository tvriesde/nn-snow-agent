using Helpdesk.Backend;
using Microsoft.Extensions.Configuration;
using ModelContextProtocol.Client;
using System.Text.Json;
using Xunit;

public sealed class McpContractTests
{
    [Fact]
    public void MetricTotalsIncludeEveryObservedBucketAndDoNotConvertMissingSamplesToZero()
    {
        using var input = JsonDocument.Parse("""
            {"results":[
              {"name":"Requests","unit":"Count","timeSeries":[{"start":"first","end":"last","totalBuckets":[0,6,30,null]}]},
              {"name":"Http5xx","unit":"Count","timeSeries":[{"start":"first","end":"last","totalBuckets":[0,0,0,2]}]}
            ]}
            """);
        using var output = JsonDocument.Parse(AzureMcpService.SanitizeMetricsEvidence(input.RootElement));
        var metrics = output.RootElement.GetProperty("metrics");
        Assert.Equal(36, metrics[0].GetProperty("totalFromReturnedSamples").GetDecimal());
        Assert.Equal(3, metrics[0].GetProperty("observedBuckets").GetInt32());
        Assert.Equal(1, metrics[0].GetProperty("missingBuckets").GetInt32());
        Assert.Equal(2, metrics[1].GetProperty("totalFromReturnedSamples").GetDecimal());
        Assert.Equal(4, metrics[1].GetProperty("observedBuckets").GetInt32());
        Assert.Equal(0, metrics[1].GetProperty("missingBuckets").GetInt32());
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[null]")]
    [InlineData("[-1]")]
    [InlineData("[\"invalid\"]")]
    public void EmptyOrInvalidMetricSamplesNeverBecomeSuccessfulZeroCounts(string buckets)
    {
        using var input = JsonDocument.Parse($$"""
            {"results":[{"name":"Requests","unit":"Count","timeSeries":[{"totalBuckets":{{buckets}}}]}]}
            """);
        Assert.Throws<InvalidOperationException>(() => AzureMcpService.SanitizeMetricsEvidence(input.RootElement));
    }

    [Fact]
    public async Task PackagedPinnedServerAdvertisesTheExpectedSafeSchemasWithoutAzureCalls()
    {
        var executable = Environment.GetEnvironmentVariable("MCP_CONTRACT_EXECUTABLE");
        if (string.IsNullOrWhiteSpace(executable)) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        await using var client = await McpClient.CreateAsync(new StdioClientTransport(new()
        {
            Command = executable,
            Arguments = ["server", "start", "--transport", "stdio", "--read-only", "--mode", "all",
                .. AzureToolPolicy.AllowedTools.SelectMany(t => new[] { "--tool", t }),
                "--outgoing-auth-strategy", "UseHostingEnvironmentIdentity", "--disable-proxy-tools"],
            InheritEnvironmentVariables = false,
            EnvironmentVariables = AzureMcpService.ChildEnvironment("11111111-1111-1111-1111-111111111111"),
            StandardErrorLines = _ => { }
        }), cancellationToken: timeout.Token);
        var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
        Assert.Equal(AzureToolPolicy.AllowedTools.Order(), tools.Select(t => t.Name).Order());
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Azure:SubscriptionId"] = "sub", ["Azure:TenantId"] = "tenant",
            ["Azure:ApplicationResources:0:Alias"] = "backend",
            ["Azure:ApplicationResources:0:ResourceId"] = "/subscriptions/sub/resourceGroups/demo/providers/Microsoft.Web/sites/backend",
            ["Azure:LogAnalyticsWorkspace"] = "workspace",
            ["Azure:ApplicationInsightsResourceId"] = "/subscriptions/sub/resourceGroups/demo/providers/microsoft.insights/components/demo"
        }).Build();
        var policy = new AzureToolPolicy(config);
        foreach (var operation in new[] { "metrics", "availability", "activity", "health" })
        {
            var request = policy.Build("backend", operation);
            var tool = Assert.Single(tools, t => t.Name == request.Tool);
            AzureMcpService.ValidateSchema(tool.JsonSchema, request.Arguments);
        }
        foreach (var name in new[] { AzureToolPolicy.HealthModelsListTool, AzureToolPolicy.HealthModelsGetTool })
        {
            var arguments = new Dictionary<string, object?> { ["subscription"] = "sub", ["tenant"] = "tenant", ["resource-group"] = "demo" };
            if (name == AzureToolPolicy.HealthModelsGetTool) arguments["health-model"] = "demo-health";
            AzureMcpService.ValidateSchema(Assert.Single(tools, t => t.Name == name).JsonSchema, arguments);
        }
    }
}
