using System.Diagnostics;
using Microsoft.ApplicationInsights;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Helpdesk.Indexer;

public sealed class AvailabilityProbeFunction(IHttpClientFactory clients, TelemetryClient telemetry,
    IConfiguration configuration, ILogger<AvailabilityProbeFunction> logger)
{
    [Function("ProbeHelpdeskAvailability")]
    public async Task RunAsync([TimerTrigger("%IndexingSchedule%")] TimerInfo timer,
        CancellationToken cancellationToken)
    {
        var targets = configuration.GetSection("Availability:Targets").GetChildren().ToArray();
        foreach (var target in targets)
        {
            var name = target["Name"];
            var resourceId = target["ResourceId"];
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(resourceId) ||
                !Uri.TryCreate(target["Url"], UriKind.Absolute, out var uri) ||
                uri.Scheme != "https" || !uri.Host.EndsWith(".azurewebsites.net", StringComparison.OrdinalIgnoreCase) ||
                !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query))
                throw new InvalidOperationException("Availability targets require a named Azure resource and HTTPS App Service URL.");
            var started = DateTimeOffset.UtcNow;
            var elapsed = Stopwatch.StartNew();
            var success = false;
            var message = "No response";
            try
            {
                using var response = await clients.CreateClient("availability")
                    .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                success = response.IsSuccessStatusCode;
                message = $"HTTP {(int)response.StatusCode}";
            }
            catch (HttpRequestException ex)
            {
                message = $"Network failure: {ex.HttpRequestError}";
                logger.LogWarning("Availability probe {Target} failed: {Reason}.", name, message);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                message = "Probe timed out";
                logger.LogWarning("Availability probe {Target} timed out.", name);
            }
            telemetry.TrackAvailability(name, started, elapsed.Elapsed, "Function probe (single location)", success,
                message, new Dictionary<string, string>
                {
                    ["TargetResourceId"] = resourceId,
                    ["schedule"] = configuration["IndexingSchedule"] ?? "0 */15 * * * *",
                    ["source"] = "real scheduled probe"
                });
        }
    }
}
