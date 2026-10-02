using System.Text.Json;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Helpdesk.Backend;

public sealed record ApplicationHealthResult(string Report, AzureEvidence? Evidence, string? Warning);

public sealed class ApplicationHealthSkill
{
    public const string Name = "azure-health-model-state";
    public const int MaximumModels = 20;
    public string Instructions { get; }
    private readonly IConfiguration config;
    private readonly IHealthModelMcp mcp;
    private readonly ILogger<ApplicationHealthSkill> logger;

    public ApplicationHealthSkill(IConfiguration config, IHealthModelMcp mcp, ILogger<ApplicationHealthSkill> logger)
    {
        this.config = config;
        this.mcp = mcp;
        this.logger = logger;
        var path = Path.Combine(AppContext.BaseDirectory, "skills", Name, "SKILL.md");
        var document = File.ReadAllText(path);
        var goal = document.IndexOf("## Goal", StringComparison.Ordinal);
        var prerequisites = document.IndexOf("## Prerequisites", StringComparison.Ordinal);
        var workflow = document.IndexOf("## Workflow", StringComparison.Ordinal);
        var troubleshooting = document.IndexOf("## Troubleshooting", StringComparison.Ordinal);
        if (goal < 0 || prerequisites <= goal || workflow <= prerequisites || troubleshooting <= workflow)
            throw new InvalidOperationException("The packaged application health skill is invalid.");
        // Editor setup and historical observations are not runtime instructions or evidence.
        Instructions = document[goal..prerequisites] + document[workflow..troubleshooting] + """

            Hosted application adaptation: invoke GetApplicationHealth to execute this skill.
            This tool uses the packaged Azure MCP server in individual-tool mode, discovers and validates
            both health model command schemas, and uses the dedicated managed identity.
            Search ONLY the server-configured Azure subscription; never discover or access other subscriptions.
            Do not use subscription_list, CLI, REST, browser, cached results or Resource Health as a fallback.
            The tool returns the authoritative report. Do not replace or reinterpret its evaluated state.
            Component-level health is unavailable; never guess entity names.
            """;
    }

    public async Task<ApplicationHealthResult> ExecuteAsync(string input, string kind, string? resourceGroup,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var subscription = config["Azure:SubscriptionId"] ?? "";
        var subscriptionLabel = config["Azure:SubscriptionName"] is { Length: > 0 } name ? $"{name} ({subscription})" : subscription;
        var commands = new List<string>();
        var observedAt = DateTimeOffset.UtcNow;
        ApplicationHealthResult Unknown(string reason) => new(
            $"Health: Unknown\n\nI couldn't verify the health of '{input}'.\n\n{reason}\n\n" +
            $"Checked at {observedAt:HH:mm:ss} UTC on {observedAt:yyyy-MM-dd}. No health was inferred from deployment success or missing alerts.",
            null, reason);
        if (string.IsNullOrWhiteSpace(input) || input.Length > 500 || input.Any(char.IsControl) ||
            kind is not ("application" or "tag" or "model" or "resourceId"))
            return Unknown("Invalid health query. Supply an application, explicit tag, health model name or health model resource ID.");
        if (!Guid.TryParse(subscription, out _))
            return Unknown("The authorized Azure subscription is not configured.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            var query = input.Trim();
            string? tagKey = kind == "application" ? "application" : null;
            string? tagValue = kind == "application" ? query : null;
            if (kind == "tag")
            {
                var split = query.IndexOfAny(['=', ':']);
                if (split <= 0 || split == query.Length - 1) return Unknown("Use an explicit tag such as application=orders.");
                tagKey = query[..split].Trim();
                tagValue = query[(split + 1)..].Trim();
                if (tagKey.Length == 0 || tagValue.Length == 0) return Unknown("The tag key and value cannot be empty.");
            }
            if (kind == "resourceId")
            {
                var parts = query.Split('/');
                if (parts.Length != 9 || !parts[1].Equals("subscriptions", StringComparison.OrdinalIgnoreCase) ||
                    !parts[2].Equals(subscription, StringComparison.OrdinalIgnoreCase) ||
                    !parts[3].Equals("resourceGroups", StringComparison.OrdinalIgnoreCase) ||
                    !parts[5].Equals("providers", StringComparison.OrdinalIgnoreCase) ||
                    !parts[6].Equals("Microsoft.CloudHealth", StringComparison.OrdinalIgnoreCase) ||
                    !parts[7].Equals("healthmodels", StringComparison.OrdinalIgnoreCase))
                    return Unknown("Only health model IDs in the authorized subscription are allowed.");
                if (resourceGroup is not null && !resourceGroup.Equals(parts[4], StringComparison.OrdinalIgnoreCase))
                    return Unknown("The resource group does not match the supplied health model ID.");
                resourceGroup = parts[4];
                query = parts[8];
                kind = "model";
            }
            async Task<JsonElement> Get(string group, string model)
            {
                commands.Add(AzureToolPolicy.HealthModelsGetTool);
                var result = await mcp.ReadAsync(group, model, timeout.Token);
                var healthModel = result.GetProperty("healthModel");
                var expectedId = $"/subscriptions/{subscription}/resourceGroups/{group}/providers/Microsoft.CloudHealth/healthmodels/{model}";
                if (!string.Equals(healthModel.GetProperty("id").GetString(), expectedId, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(healthModel.GetProperty("name").GetString(), model, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(healthModel.GetProperty("resourceGroup").GetString(), group, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Azure MCP returned a different health model than requested.");
                return healthModel;
            }
            var matches = new List<(JsonElement Model, string? Tag)>();
            var candidates = new List<string>();
            if (kind == "model" && resourceGroup is not null)
                matches.Add((await Get(resourceGroup, query), null));
            else
            {
                commands.Add(AzureToolPolicy.HealthModelsListTool);
                var listed = await mcp.ReadAsync(resourceGroup, null, timeout.Token);
                var models = listed.GetProperty("healthModels");
                if (models.ValueKind != JsonValueKind.Array || models.GetArrayLength() > MaximumModels)
                    return Unknown($"The health-model discovery limit ({MaximumModels}) was exceeded or the list was invalid. Specify a model and resource group; partial scans cannot determine health.");
                foreach (var model in models.EnumerateArray())
                {
                    var modelName = model.GetProperty("name").GetString() ?? throw new JsonException();
                    var group = model.GetProperty("resourceGroup").GetString() ?? throw new JsonException();
                    if (resourceGroup is not null && !group.Equals(resourceGroup, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Azure MCP returned a model outside the requested resource group.");
                    if (kind == "model")
                    {
                        if (modelName.Equals(query, StringComparison.OrdinalIgnoreCase))
                            matches.Add((await Get(group, modelName), null));
                        continue;
                    }
                    var detail = await Get(group, modelName);
                    string? storedTag = null;
                    if (detail.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var tag in tags.EnumerateObject())
                            if (tag.Name.Equals(tagKey, StringComparison.OrdinalIgnoreCase) &&
                                tag.Value.ValueKind == JsonValueKind.String &&
                                string.Equals(tag.Value.GetString()?.Trim(), tagValue, StringComparison.OrdinalIgnoreCase))
                                storedTag = $"{tag.Name}={tag.Value.GetString()}";
                    }
                    if (storedTag is not null) matches.Add((detail, storedTag));
                    else if (modelName.Contains(tagValue!, StringComparison.OrdinalIgnoreCase))
                    {
                        var actualTags = detail.TryGetProperty("tags", out var candidateTags) && candidateTags.ValueKind == JsonValueKind.Object
                            ? string.Join(", ", candidateTags.EnumerateObject().Where(t => t.Name.Equals(tagKey, StringComparison.OrdinalIgnoreCase))
                                .Select(t => $"{t.Name}={t.Value}"))
                            : "";
                        candidates.Add($"{modelName} in {group}, subscription {subscription}, searched tag: {(actualTags.Length == 0 ? "absent" : actualTags)}");
                    }
                }
            }
            observedAt = DateTimeOffset.UtcNow;
            if (matches.Count == 0)
                return Unknown($"Azure MCP found no matching health model for {(tagKey is null ? query : $"{tagKey}={tagValue}")}." +
                    (candidates.Count == 0 ? "" : "\nUnconfirmed name-based candidates (no health attributed):\n" + string.Join("\n", candidates) +
                        "\nSpecify the model name and resource group to confirm which to inspect."));
            if (matches.Count > 1)
                return Unknown("Multiple health models match. Specify the model name and resource group:\n" +
                    string.Join("\n", matches.Select(m => $"{m.Model.GetProperty("name").GetString()} in {m.Model.GetProperty("resourceGroup").GetString()}, subscription {subscription}")));
            var selected = matches[0];
            var state = selected.Model.TryGetProperty("healthState", out var health) && health.ValueKind == JsonValueKind.String
                ? health.GetString() : null;
            var knownState = state is "Healthy" or "Degraded" or "Unhealthy" or "Unknown";
            state = knownState ? state : "Unknown";
            var provisioning = selected.Model.TryGetProperty("provisioningState", out var deployed) && deployed.ValueKind == JsonValueKind.String
                ? deployed.GetString() : "not returned";
            var details = $"Application health: {state}\nSearch: {input}\n" +
                $"Health model: {selected.Model.GetProperty("name").GetString()}\nResource group: {selected.Model.GetProperty("resourceGroup").GetString()}\n" +
                $"Subscription: {subscriptionLabel}\n" +
                (selected.Tag is null ? "Matched by exact model name/resource ID.\n" : $"Verified matching tag (as stored): {selected.Tag}\n") +
                $"Provisioning state (deployment status only): {provisioning}\n" +
                $"Source: Azure MCP {AzureToolPolicy.HealthModelsGetTool}, results.healthModel.healthState\n" +
                $"Commands used: {string.Join(", ", commands.Distinct())}\nRetrieval time: {observedAt:O}\n" +
                (state == "Unknown" ? "Health has not been evaluated or is unavailable; no health was inferred.\n" : "") +
                "This is evaluated application health, not an HTTP uptime/SLA measurement. Component breakdown is unavailable through the configured MCP tools.";
            var evidence = new AzureEvidence(selected.Model.GetProperty("id").GetString()!, observedAt.ToString("O"),
                "Current evaluated application health", details);
            return new(ReadableReport(input, kind, state!, observedAt), evidence,
                state == "Unknown" ? "Azure MCP returned no recognized evaluated application health state." : null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            observedAt = DateTimeOffset.UtcNow;
            logger.LogWarning("Application health skill timed out using {Commands}", string.Join(", ", commands));
            return Unknown("Azure MCP health-model retrieval timed out. Retry or specify the exact model and resource group.");
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            observedAt = DateTimeOffset.UtcNow;
            logger.LogWarning(exception, "Application health skill failed using {Commands}", string.Join(", ", commands));
            if (exception is AzureMcpQueryException)
                return Unknown(exception.Message + " No alternate health source was used.");
            return Unknown($"Azure MCP {commands.LastOrDefault() ?? "health-model lookup"} failed ({exception.GetType().Name}). " +
                "The model may not exist, access may be denied, or the command/schema may be unavailable. No alternate health source was used.");
        }
        finally
        {
            logger.LogInformation("Application health skill completed in {ElapsedMilliseconds} ms using {McpCalls} MCP calls",
                Stopwatch.GetElapsedTime(started).TotalMilliseconds, commands.Count);
        }
    }

    private string ReadableReport(string input, string kind, string state, DateTimeOffset observedAt)
    {
        var subject = kind == "application" && input.Trim().Equals(config["Azure:HealthApplication"], StringComparison.OrdinalIgnoreCase)
            ? "The IT helpdesk" : kind == "model" ? $"The health model '{input}'" : $"The application matched by '{input}'";
        var explanation = state switch
        {
            "Healthy" => $"{subject} is healthy according to its Azure Monitor health model. This does not guarantee that every user journey is working.",
            "Degraded" => $"{subject} is degraded according to its Azure Monitor health model. This is a warning-level health state, not proof that the whole application is unavailable.",
            "Unhealthy" => $"{subject} is unhealthy according to its Azure Monitor health model. Treat this as a reported critical health condition and contact the service desk if you are affected.",
            _ => $"Azure Monitor has not provided a recognized evaluated health state for '{input}', so I can't confirm whether it is healthy."
        };
        var breakdown = state is "Degraded" or "Unhealthy"
            ? "\n\nWhich service needs attention?\nThe installed Azure MCP server returns the overall state, but not component health or failing signals. I can't verify which service contributed to this state or name a root cause. A degraded model does not mean every service is degraded."
            : "";
        return $"Health: {state}\n\n{explanation}{breakdown}\n\n" +
            $"Checked at {observedAt:HH:mm:ss} UTC on {observedAt:yyyy-MM-dd} using Azure MCP. " +
            "This is application health, not an HTTP uptime measurement. Open evidence details for the model, source and technical verification.";
    }
}
