using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure.Identity;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using Helpdesk.Backend;
using Helpdesk.Core;
using Helpdesk.Evaluation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
var arguments = args.Skip(1).Chunk(2).ToDictionary(p => p[0], p => p.Length == 2 ? p[1] :
    throw new ArgumentException($"Missing value for {p[0]}"), StringComparer.Ordinal);
string Required(string key) => arguments.GetValueOrDefault(key) ?? throw new ArgumentException($"Missing {key}");
string Option(string key, string fallback) => arguments.GetValueOrDefault(key, fallback);
void Write(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, json), new UTF8Encoding(false));
var command = args.FirstOrDefault() ?? throw new ArgumentException("Use seed, snapshot, probe or run.");
var output = Path.GetFullPath(Required("--output"));
Directory.CreateDirectory(Path.GetDirectoryName(output)!);

if (command == "seed")
{
    var root = Path.GetFullPath(Required("--root"));
    var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "data", "servicenow", "manifest.json")));
    var documents = new List<KnowledgeDocument>();
    foreach (var batch in manifest.RootElement.GetProperty("batches").EnumerateArray())
    {
        var relative = batch.GetProperty("path").GetString()!.Replace('/', Path.DirectorySeparatorChar);
        var bytes = File.ReadAllBytes(Path.Combine(root, "data", "servicenow", relative));
        var expected = batch.GetProperty("sha256").GetString();
        if (!Convert.ToHexStringLower(SHA256.HashData(bytes)).Equals(expected, StringComparison.Ordinal))
            throw new InvalidDataException($"Seed hash mismatch: {relative}");
        using var source = JsonDocument.Parse(bytes);
        foreach (var record in source.RootElement.GetProperty("result").EnumerateArray())
            documents.AddRange(ServiceNowProjection.Project(batch.GetProperty("table").GetString()!, record,
                batch.GetProperty("sequence").GetInt64()));
    }
    Write(output, documents);
    return;
}

var config = new ConfigurationBuilder().AddJsonFile(Path.GetFullPath(Required("--config")))
    .AddEnvironmentVariables().Build();
// Explicit Azure CLI credentials avoid accidentally selecting a different local identity.
var credential = new AzureCliCredential();
var search = new SearchClient(new Uri(config["Search:Endpoint"] ?? throw new InvalidOperationException("Search endpoint required.")),
    config["Search:IndexName"] ?? "servicenow-knowledge", credential);
var knowledge = new KnowledgeService(config, credential);

if (command == "snapshot")
{
    var response = await search.SearchAsync<SearchDocument>("*", new SearchOptions
        { Filter = "visibility eq 'employee'", Size = 1000, IncludeTotalCount = true });
    var documents = new List<SearchDocument>();
    await foreach (var result in response.Value.GetResultsAsync()) documents.Add(result.Document);
    if (response.Value.TotalCount != documents.Count)
        throw new InvalidDataException("The live snapshot is incomplete; refusing a truncated corpus fingerprint.");
    Write(output, documents);
    return;
}

var items = File.ReadLines(Required("--dataset")).Where(line => !string.IsNullOrWhiteSpace(line))
    .Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
if (arguments.TryGetValue("--ids", out var selectedIds))
{
    var ids = selectedIds.Split(',').ToHashSet(StringComparer.Ordinal);
    if (ids.Count != selectedIds.Split(',').Length ||
        ids.Except(items.Select(item => item.GetProperty("id").GetString()!)).Any())
        throw new ArgumentException("Evaluation IDs must be unique and present in the dataset.");
    items = items.Where(item => ids.Contains(item.GetProperty("id").GetString()!)).ToArray();
}
if (command == "probe")
{
    var probes = new List<object>();
    foreach (var item in items)
    {
        var query = item.GetProperty("query").GetString()!;
        var sources = await knowledge.SearchAsync(query, default);
        probes.Add(new { id = item.GetProperty("id").GetString(), query, sources });
    }
    Write(output, probes);
    return;
}
if (command != "run") throw new ArgumentException($"Unknown command {command}");

var modelId = Required("--model");
var catalog = new ModelCatalog(config);
if (!catalog.TryResolve(modelId, out _)) throw new ArgumentException($"Unknown model {modelId}");
var repeats = int.Parse(Option("--repeats", "3"));
var limit = int.Parse(Option("--limit", items.Length.ToString()));
if (repeats is < 1 or > 20 || limit < 1) throw new ArgumentOutOfRangeException(nameof(repeats));
using var writer = new StreamWriter(new FileStream(output, FileMode.CreateNew), new UTF8Encoding(false));
var failed = 0;
for (var repeat = 0; repeat < repeats; repeat++)
foreach (var item in items.Take(limit))
{
    var trace = new EvaluationTrace();
    var unavailable = new UnavailableAzureTools(trace);
    var health = new ApplicationHealthSkill(config, unavailable, NullLogger<ApplicationHealthSkill>.Instance);
    var agent = new HelpdeskAgent(config, new RecordingKnowledge(knowledge, trace), unavailable, health,
        new RecordingFactory(trace), catalog);
    var query = item.GetProperty("query").GetString()!;
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
    ChatResponse? response = null;
    try
    {
        response = await agent.RunAsync(new Conversation(Guid.NewGuid().ToString("D"), "evaluation",
            DateTimeOffset.UtcNow), query, false, timeout.Token, modelId);
    }
    catch (OperationCanceledException)
    {
        trace.Errors.Add("Evaluation timed out or was cancelled.");
    }
    var sources = trace.Retrievals.SelectMany(r => r.Sources).DistinctBy(s => s.Id).ToArray();
    var status = trace.Succeeded(response) ? "ok" : "error";
    if (status != "ok") failed++;
    await writer.WriteLineAsync(JsonSerializer.Serialize(new
    {
        id = item.GetProperty("id").GetString(), model = modelId, repeat,
        category = item.GetProperty("category").GetString(), query,
        ground_truth = item.GetProperty("ground_truth").GetString(),
        response = response?.Answer ?? "", context = string.Join("\n\n", sources.Select(s => $"[{s.Id}] {s.Title}\n{s.Snippet}")),
        retrieved_sources = sources, citations = response?.KnowledgeSources.Select(s => s.Id).ToArray() ?? [],
        status, warnings = response?.Warnings ?? [], errors = trace.Errors, processing = response?.Processing,
        retrievals = trace.Retrievals, tool_calls = trace.Tools, rounds = trace.Rounds, raw_answer = trace.RawAnswer
    }, json));
    await writer.FlushAsync();
    Console.Error.WriteLine($"{modelId} {item.GetProperty("id").GetString()} repeat={repeat} status={status}");
}
if (failed > 0) throw new InvalidOperationException($"{failed} rows failed; inspect the persisted response rows.");
