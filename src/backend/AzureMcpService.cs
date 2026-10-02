using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Helpdesk.Backend;

public sealed class AzureToolPolicy
{
    public const string MetricsTool = "monitor_metrics_query";
    public const string LogsTool = "monitor_workspace_log_query";
    public const string ActivityTool = "monitor_activitylog_list";
    public const string HealthTool = "resourcehealth_availability-status_get";
    public const string HealthModelsListTool = "monitor_healthmodels_list";
    public const string HealthModelsGetTool = "monitor_healthmodels_get";
    public static readonly string[] AllowedTools =
        [MetricsTool, LogsTool, ActivityTool, HealthTool, HealthModelsListTool, HealthModelsGetTool];
    private readonly IConfiguration config;
    public AzureToolPolicy(IConfiguration config) => this.config = config;
    public ApplicationResource? Resolve(string alias) => config.GetSection("Azure:ApplicationResources")
        .Get<ApplicationResource[]>()?.SingleOrDefault(r => r.Alias.Equals(alias, StringComparison.OrdinalIgnoreCase));

    public (string Tool, Dictionary<string, object?> Arguments, string ResourceId) Build(string alias, string operation)
    {
        var resource = Resolve(alias) ?? throw new InvalidOperationException("No authorized live Azure mapping exists for that application.");
        var segments = resource.ResourceId.Split('/');
        if (segments.Length != 9 || segments[1] != "subscriptions" ||
            !segments[2].Equals(config["Azure:SubscriptionId"], StringComparison.OrdinalIgnoreCase) ||
            !segments[5].Equals("providers", StringComparison.OrdinalIgnoreCase) ||
            !segments[6].Equals("Microsoft.Web", StringComparison.OrdinalIgnoreCase) || segments[7] != "sites")
            throw new InvalidOperationException("Only configured App Service resources in the fixed subscription are supported.");
        var arguments = new Dictionary<string, object?>
        {
            ["subscription"] = config["Azure:SubscriptionId"], ["tenant"] = config["Azure:TenantId"]
        };
        if (operation == "metrics")
        {
            arguments["resource-group"] = segments[4]; arguments["resource"] = segments[8];
            arguments["resource-type"] = "Microsoft.Web/sites"; arguments["metric-namespace"] = "Microsoft.Web/sites";
            arguments["metric-names"] = "Requests,Http5xx"; arguments["interval"] = "PT1H";
            arguments["aggregation"] = "Total"; arguments["max-buckets"] = 24;
            arguments["start-time"] = DateTimeOffset.UtcNow.AddHours(-24).ToString("O");
            arguments["end-time"] = DateTimeOffset.UtcNow.ToString("O");
            return (MetricsTool, arguments, resource.ResourceId);
        }
        if (operation == "availability")
        {
            var workspace = config["Azure:LogAnalyticsWorkspace"];
            var insights = config["Azure:ApplicationInsightsResourceId"];
            if (string.IsNullOrEmpty(workspace) || string.IsNullOrEmpty(insights) ||
                !insights.StartsWith($"/subscriptions/{config["Azure:SubscriptionId"]}/", StringComparison.OrdinalIgnoreCase) ||
                insights.Contains('\'') || resource.ResourceId.Contains('\''))
                throw new InvalidOperationException("Measured availability is not configured.");
            arguments["workspace"] = workspace; arguments["table"] = "AppAvailabilityResults";
            arguments["hours"] = 24; arguments["limit"] = 1;
            arguments["query"] = $"AppAvailabilityResults | where TimeGenerated > ago(24h) | where _ResourceId =~ '{insights}' | where tostring(Properties['TargetResourceId']) =~ '{resource.ResourceId}' | summarize executed=count(), succeeded=countif(Success == true), first=min(TimeGenerated), last=max(TimeGenerated) | take 1";
            return (LogsTool, arguments, resource.ResourceId);
        }
        if (operation == "activity")
        {
            arguments["resource-name"] = segments[8]; arguments["resource-type"] = "Microsoft.Web/sites";
            arguments["resource-group"] = segments[4]; arguments["hours"] = 24.0; arguments["top"] = 10;
            return (ActivityTool, arguments, resource.ResourceId);
        }
        if (operation == "health")
        {
            arguments["resourceId"] = resource.ResourceId; arguments["resource-group"] = segments[4];
            return (HealthTool, arguments, resource.ResourceId);
        }
        throw new InvalidOperationException("Only metrics, measured availability, activity and resource health are allowed; writes, secrets and arbitrary queries are prohibited.");
    }

    // Namespace dispatchers are not granted ambient authority: exact command and exact generated arguments are required.
    public static bool ValidateDispatch(string tool, string command, IReadOnlyDictionary<string, object?> actual,
        string expectedTool, IReadOnlyDictionary<string, object?> expected) =>
        (tool == expectedTool && command == expectedTool ||
         tool == expectedTool.Split('_')[0] && command == expectedTool.Replace('_', ' ')) &&
        actual.Count == expected.Count && expected.All(p => actual.TryGetValue(p.Key, out var value) &&
            JsonSerializer.Serialize(value) == JsonSerializer.Serialize(p.Value));
}

public interface IAzureInvestigator
{
    Task<AzureEvidence> InvestigateAsync(string alias, string operation, CancellationToken cancellationToken);
}
public interface IHealthModelMcp
{
    Task<JsonElement> ReadAsync(string? resourceGroup, string? modelName, CancellationToken cancellationToken);
}
public sealed class AzureMcpQueryException(string message) : InvalidOperationException(message);
public sealed class AzureMcpService(IConfiguration config, AzureToolPolicy policy) : IAzureInvestigator, IHealthModelMcp, IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private McpClient? client;
    private IList<McpClientTool>? tools;
    public static Dictionary<string, string?> ChildEnvironment(string clientId, string? cachePath = null)
    {
        cachePath ??= Path.Combine(AppContext.BaseDirectory, ".mcp-cache");
        Directory.CreateDirectory(cachePath);
        var result = new Dictionary<string, string?>
        {
            ["AZURE_CLIENT_ID"] = clientId, ["AZURE_TOKEN_CREDENTIALS"] = "ManagedIdentityCredential",
            ["AZURE_MCP_COLLECT_TELEMETRY"] = "false",
            ["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = cachePath
        };
        foreach (var key in new[] { "PATH", "SystemRoot", "WINDIR", "DOTNET_ROOT", "IDENTITY_ENDPOINT", "IDENTITY_HEADER" })
            if (Environment.GetEnvironmentVariable(key) is { } value) result[key] = value;
        return result;
    }
    public async Task<AzureEvidence> InvestigateAsync(string alias, string operation, CancellationToken cancellationToken)
    {
        var request = policy.Build(alias, operation);
        var results = await ExecuteAsync(request.Tool, request.Arguments, cancellationToken);
        if (operation == "availability" && !HasExecutedProbeSamples(results))
            throw new InvalidOperationException("No executed availability samples were returned. Uptime is unknown, not 100 percent.");
        var summary = operation switch
        {
            "metrics" => SanitizeMetricsEvidence(results),
            "activity" or "health" => SanitizeOperationalEvidence(results, request.ResourceId, operation),
            _ => results.GetRawText()
        };
        var label = operation switch
        {
            "metrics" => "Actual Azure Monitor counts summed from returned samples; missing buckets are not zero (not HTTP uptime)",
            "availability" => "Actual executed probe samples; missing samples and partial coverage are not proof of uptime",
            "activity" => "Actual resource-scoped recent activity (maximum 10 events); temporal proximity does not establish causation",
            _ => "Actual Azure Resource Health status (not an HTTP availability test or application health model)"
        };
        return new(request.ResourceId, DateTimeOffset.UtcNow.ToString("O"),
            operation == "health" ? "Current Resource Health observation" : "Last 24 hours", $"{label}: {summary}");
    }
    public Task<JsonElement> ReadAsync(string? resourceGroup, string? modelName, CancellationToken cancellationToken)
    {
        var subscription = config["Azure:SubscriptionId"];
        if (!Guid.TryParse(subscription, out _))
            throw new InvalidOperationException("The authorized health model subscription is not configured.");
        if (resourceGroup is not null && !System.Text.RegularExpressions.Regex.IsMatch(resourceGroup, @"^[a-zA-Z0-9_.()-]{1,90}$") ||
            modelName is not null && (!System.Text.RegularExpressions.Regex.IsMatch(modelName, @"^[a-zA-Z0-9_-]{1,100}$") || resourceGroup is null))
            throw new InvalidOperationException("A valid health model name and resource group are required.");
        var arguments = new Dictionary<string, object?>
        {
            ["subscription"] = subscription, ["tenant"] = config["Azure:TenantId"]
        };
        if (resourceGroup is not null) arguments["resource-group"] = resourceGroup;
        if (modelName is not null) arguments["health-model"] = modelName;
        return ExecuteAsync(modelName is null ? AzureToolPolicy.HealthModelsListTool : AzureToolPolicy.HealthModelsGetTool,
            arguments, cancellationToken);
    }
    private async Task<JsonElement> ExecuteAsync(string toolName, Dictionary<string, object?> arguments, CancellationToken cancellationToken)
    {
        if (!AzureToolPolicy.AllowedTools.Contains(toolName, StringComparer.Ordinal))
            throw new InvalidOperationException("Forbidden MCP command.");
        if (!config.GetValue<bool>("AzureMcp:Enabled")) throw new InvalidOperationException("Live Azure investigation is disabled.");
        await gate.WaitAsync(cancellationToken);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(25));
            if (client is null)
            {
                var executable = config["AzureMcp:ExecutablePath"];
                var identity = config["AzureMcp:ClientId"];
                if (!Guid.TryParse(identity, out _) || string.IsNullOrEmpty(executable) || !Path.IsPathFullyQualified(executable) ||
                    !File.Exists(executable) || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("IDENTITY_ENDPOINT")) ||
                    string.IsNullOrEmpty(Environment.GetEnvironmentVariable("IDENTITY_HEADER")))
                    throw new InvalidOperationException("A packaged executable and dedicated hosted managed identity are required.");
                var transport = new StdioClientTransport(new()
                {
                    Name = "Pinned read-only Azure MCP",
                    Command = executable,
                    Arguments = ["server", "start", "--transport", "stdio", "--read-only", "--mode", "all",
                        .. AzureToolPolicy.AllowedTools.SelectMany(t => new[] { "--tool", t }),
                        "--outgoing-auth-strategy", "UseHostingEnvironmentIdentity", "--disable-proxy-tools"],
                    InheritEnvironmentVariables = false,
                    EnvironmentVariables = ChildEnvironment(identity, config["AzureMcp:CachePath"]),
                    StandardErrorLines = _ => { }
                });
                client = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);
                tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
                foreach (var tool in tools)
                    if (!AzureToolPolicy.AllowedTools.Contains(tool.Name, StringComparer.Ordinal))
                        throw new InvalidOperationException("Unexpected MCP tool contract.");
            }
            if (toolName is AzureToolPolicy.HealthModelsListTool or AzureToolPolicy.HealthModelsGetTool)
                foreach (var required in new[] { AzureToolPolicy.HealthModelsListTool, AzureToolPolicy.HealthModelsGetTool })
                    if (!tools!.Any(t => t.Name == required))
                        throw new AzureMcpQueryException($"Azure MCP command {required} is unavailable; upgrade the packaged server.");
            var selected = tools!.SingleOrDefault(t => t.Name == toolName) ??
                throw new AzureMcpQueryException($"Azure MCP command {toolName} is unavailable.");
            ValidateSchema(selected.JsonSchema, arguments);
            if (!AzureToolPolicy.ValidateDispatch(toolName, toolName, arguments, toolName, arguments))
                throw new InvalidOperationException("Forbidden MCP command.");
            var result = await client.CallToolAsync(toolName, arguments, cancellationToken: timeout.Token);
            var texts = result.Content.OfType<TextContentBlock>().Select(t => t.Text);
            var summary = string.Join("\n", texts);
            if (summary.Length > 16000) throw new InvalidOperationException("Azure MCP result exceeded its safe bound.");
            using var json = JsonDocument.Parse(summary);
            if (!json.RootElement.TryGetProperty("status", out var status) || !status.TryGetInt32(out var code))
                throw new AzureMcpQueryException($"Azure MCP {toolName} returned no valid operation status.");
            if (code == 404)
                throw new AzureMcpQueryException($"Azure MCP {toolName}: the requested health model/resource was not found in the requested resource group (404).");
            if (code is 401 or 403)
                throw new AzureMcpQueryException($"Azure MCP {toolName}: authentication or Reader access was denied ({code}).");
            if (result.IsError == true || code != 200 ||
                !json.RootElement.TryGetProperty("results", out var results) || results.ValueKind == JsonValueKind.Null)
                throw new AzureMcpQueryException($"Azure MCP {toolName} returned an unsuccessful operation ({code}).");
            return results.Clone();
        }
        catch
        {
            if (client is not null) await client.DisposeAsync();
            client = null; tools = null;
            throw;
        }
        finally { gate.Release(); }
    }
    public static void ValidateSchema(JsonElement schema, Dictionary<string, object?> arguments)
    {
        if (!schema.TryGetProperty("properties", out var properties) ||
            arguments.Keys.Any(k => !properties.TryGetProperty(k, out _)))
            throw new InvalidOperationException("MCP schema changed; invocation refused.");
        if (schema.TryGetProperty("required", out var required) &&
            required.EnumerateArray().Any(k => !arguments.ContainsKey(k.GetString()!)))
            throw new InvalidOperationException("MCP required arguments changed; invocation refused.");
        foreach (var argument in arguments)
        {
            var property = properties.GetProperty(argument.Key);
            if (!property.TryGetProperty("type", out var type)) throw new InvalidOperationException("MCP argument type is unspecified.");
            var expected = argument.Value is int ? "integer" : argument.Value is double ? "number" : "string";
            var accepted = type.ValueKind == JsonValueKind.String ? type.GetString() == expected :
                type.ValueKind == JsonValueKind.Array && type.EnumerateArray().Any(t => t.GetString() == expected);
            if (!accepted) throw new InvalidOperationException("MCP argument type changed; invocation refused.");
        }
    }
    public static bool HasExecutedProbeSamples(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (value.TryGetProperty("executed", out var count))
                return count.ValueKind == JsonValueKind.Number && count.TryGetInt64(out var number) && number > 0;
            return value.EnumerateObject().Any(property => HasExecutedProbeSamples(property.Value));
        }
        return value.ValueKind == JsonValueKind.Array && value.EnumerateArray().Any(HasExecutedProbeSamples);
    }
    public static string SanitizeMetricsEvidence(JsonElement results)
    {
        var metrics = results.GetProperty("results");
        var summaries = new List<object>();
        foreach (var name in new[] { "Requests", "Http5xx" })
        {
            var metric = metrics.EnumerateArray().Single(m => m.GetProperty("name").GetString() == name);
            var series = metric.GetProperty("timeSeries");
            if (metric.GetProperty("unit").GetString() != "Count" || series.GetArrayLength() != 1)
                throw new InvalidOperationException("Unexpected MCP metric series contract.");
            var buckets = series[0].GetProperty("totalBuckets");
            if (buckets.GetArrayLength() is < 1 or > 24)
                throw new InvalidOperationException("Unexpected MCP metric bucket count.");
            decimal total = 0;
            var observed = 0;
            foreach (var bucket in buckets.EnumerateArray())
            {
                if (bucket.ValueKind == JsonValueKind.Null) continue;
                if (bucket.ValueKind != JsonValueKind.Number || !bucket.TryGetDecimal(out var count) || count < 0)
                    throw new InvalidOperationException("Invalid MCP metric count.");
                total += count;
                observed++;
            }
            if (observed == 0) throw new InvalidOperationException("No observed MCP metric samples.");
            summaries.Add(new
            {
                name, totalFromReturnedSamples = total, observedBuckets = observed,
                missingBuckets = buckets.GetArrayLength() - observed,
                firstReturnedBucket = series[0].GetProperty("start").GetString(),
                lastReturnedBucket = series[0].GetProperty("end").GetString()
            });
        }
        return JsonSerializer.Serialize(new { metrics = summaries });
    }
    public static string SanitizeOperationalEvidence(JsonElement results, string resourceId, string operation)
    {
        var rows = new List<Dictionary<string, string>>();
        void Visit(JsonElement value)
        {
            if (rows.Count >= 10) return;
            if (value.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in value.EnumerateArray()) Visit(child);
                return;
            }
            if (value.ValueKind != JsonValueKind.Object) return;
            var marker = operation == "activity" ? "eventTimestamp" : "availabilityState";
            if (value.TryGetProperty(marker, out _))
            {
                if (operation == "activity" && (!value.TryGetProperty("resourceId", out var id) ||
                    !string.Equals(id.GetString(), resourceId, StringComparison.OrdinalIgnoreCase))) return;
                var row = new Dictionary<string, string>();
                var keys = operation == "activity" ? new[] { "eventTimestamp", "operationName", "status" } :
                    new[] { "availabilityState", "reasonType", "occurredTime", "reportedTime" };
                foreach (var key in keys)
                {
                    if (!value.TryGetProperty(key, out var field)) continue;
                    if (field.ValueKind == JsonValueKind.Object && field.TryGetProperty("value", out var nested)) field = nested;
                    if (field.ValueKind != JsonValueKind.String) continue;
                    var text = field.GetString()!;
                    row[key] = text[..Math.Min(text.Length, 300)];
                }
                rows.Add(row);
                return;
            }
            foreach (var property in value.EnumerateObject()) Visit(property.Value);
        }
        Visit(results);
        if (operation == "health" && rows.Count == 0) throw new InvalidOperationException("No verified resource health state was returned.");
        return JsonSerializer.Serialize(rows);
    }
    public async ValueTask DisposeAsync()
    {
        if (client is not null) await client.DisposeAsync();
        gate.Dispose();
    }
}
