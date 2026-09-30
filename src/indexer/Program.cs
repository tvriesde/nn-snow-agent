using Azure.Core;
using Azure.Identity;
using Azure.Search.Documents;
using Azure.Storage.Blobs;
using Helpdesk.Indexer;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices((context, services) =>
    {
        var configuration = context.Configuration;
        var credential = configuration.GetValue<bool>("Authentication:UseDeveloperCredential")
            ? (TokenCredential)new AzureCliCredential()
            : new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned);
        services.AddSingleton(credential);
        services.AddSingleton(new BlobServiceClient(
            new Uri(Required(configuration, "Source:BlobServiceUri")), credential));
        services.AddSingleton(new SearchClient(new Uri(Required(configuration, "Search:Endpoint")),
            configuration["Search:IndexName"] ?? "servicenow-knowledge", credential));
        services.AddSingleton<BlobSourceReader>();
        services.AddSingleton<SearchKnowledgeWriter>();
        services.AddHttpClient("availability", client => client.Timeout = TimeSpan.FromSeconds(20))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddApplicationInsightsTelemetryWorkerService(options => options.EnableAdaptiveSampling = false);
        services.Configure<TelemetryConfiguration>(telemetry => telemetry.SetAzureTokenCredential(credential));
        services.ConfigureFunctionsApplicationInsights();
    })
    .Build();

await host.RunAsync();

static string Required(IConfiguration config, string key) =>
    !string.IsNullOrWhiteSpace(config[key]) ? config[key]! :
        throw new InvalidOperationException($"Configuration {key} is required.");
