using System.Security.Claims;
using System.Text.Json;

namespace Helpdesk.Backend;

public sealed record ChatRequest(string Message, string? ConversationId = null, string? ModelId = null);
public sealed record KnowledgeSource(string Id, string Number, string Title, string Snippet, string Application);
public sealed record AzureEvidence(string ResourceId, string ObservedAt, string Window, string Summary);
public sealed record ChatResponse(string ConversationId, string Answer, IReadOnlyList<KnowledgeSource> KnowledgeSources,
    IReadOnlyList<AzureEvidence> AzureEvidence, IReadOnlyList<string> Warnings, AnswerProcessing? Processing = null);
public sealed record ExampleQuestion(string Id, string Category, string Question, string Evidence);
public sealed record ApplicationResource(string Alias, string ResourceId);

public static class AccessPolicy
{
    public static bool HasScope(ClaimsPrincipal user, string scope) =>
        user.Identity?.IsAuthenticated == true &&
        user.FindAll("scp").Any(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(scope, StringComparer.Ordinal));

    public static string? UserKey(ClaimsPrincipal user) =>
        user.FindFirstValue("tid") is { } tenant && user.FindFirstValue("oid") is { } oid ? $"{tenant}:{oid}" : null;

    public static bool ValidRequest(ChatRequest request) =>
        !string.IsNullOrWhiteSpace(request.Message) && request.Message.Length <= 4000 &&
        (request.ConversationId is null || Guid.TryParseExact(request.ConversationId, "D", out _));
}

public sealed class EvidenceLedger
{
    public Dictionary<string, KnowledgeSource> Knowledge { get; } = new(StringComparer.Ordinal);
    public List<AzureEvidence> Azure { get; } = [];
    public List<string> HealthReports { get; } = [];
    public HashSet<string> Warnings { get; } = new(StringComparer.Ordinal);
    public int ToolCalls { get; private set; }
    public void CountTool()
    {
        if (++ToolCalls > 6) throw new InvalidOperationException("Tool call budget exceeded.");
    }
    public ChatResponse Finish(string conversationId, string modelText)
    {
        try
        {
            var text = modelText.Trim();
            if (text.StartsWith("```")) text = text[(text.IndexOf('\n') + 1)..].TrimEnd('`').Trim();
            var payload = JsonSerializer.Deserialize<ModelAnswer>(text, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (payload is null || string.IsNullOrWhiteSpace(payload.Answer) || payload.Answer.Length > 12000)
                throw new JsonException();
            var ids = payload.KnowledgeSourceIds ?? [];
            if (ids.Any(id => !Knowledge.ContainsKey(id)))
            {
                Warnings.Add("The model returned an unverified citation; its answer was withheld.");
                return Response("I could not verify the answer's citations. Please rephrase your question or contact the service desk.", []);
            }
            // Sources are server-owned; model-produced snippets, timestamps and telemetry never enter the API.
            return Response(payload.Answer, ids.Distinct().Select(id => Knowledge[id]).ToArray());
        }
        catch (JsonException)
        {
            Warnings.Add("The model returned an invalid response; no unverified answer was shown.");
            return Response("I could not produce a verified answer. Please try again or contact the service desk.", []);
        }
        ChatResponse Response(string answer, KnowledgeSource[] sources) =>
            new(conversationId, HealthReports.Count > 0 ? string.Join("\n\n", HealthReports) : answer,
                HealthReports.Count > 0 ? [] : sources, Azure.ToArray(), Warnings.ToArray());
    }
    private sealed record ModelAnswer(string Answer, string[]? KnowledgeSourceIds);
}

public static class Examples
{
    public static readonly ExampleQuestion[] Catalog =
    [
        new("claims-signin", "Identity", "I cannot sign in to Claims Workbench after changing my password.", "Employee identity/session KB and sanitized resolved incident"),
        new("mfa-phone", "Identity", "My MFA prompt goes to my old phone. What should I do?", "MFA recovery KB; no bypass advice"),
        new("policy-loop", "Applications", "Policy Administration sends me around a login loop.", "SSO KB and known problem"),
        new("broker-url", "Applications", "The Broker Portal URL returns 404. Is there a new address?", "Published URL-change KB"),
        new("vpn", "Network", "My VPN connects but Underwriting Desktop does not load.", "VPN/DNS KB and sanitized resolution"),
        new("attachments", "Applications", "I get access denied when opening a claim attachment.", "Document Vault permissions KB"),
        new("analytics", "Applications", "Actuarial Analytics is slow during month-end processing.", "Known problem and safe troubleshooting"),
        new("export", "Applications", "Why has my Finance Reporting export been blocked?", "Synthetic DLP/export KB"),
        new("helpdesk-live", "Azure evidence", "Is this IT helpdesk application healthy right now?", "Packaged health-model skill and fresh evaluated Azure MCP application health; not HTTP uptime"),
        new("helpdesk-history", "Azure evidence", "What was the helpdesk backend's availability over the last 24 hours?", "Measured probe events and coverage, if configured"),
        new("helpdesk-outage", "Azure evidence", "The helpdesk URL stopped responding. Is this a known issue or an Azure outage?", "Indexed knowledge and actual Azure evidence"),
        new("change", "Azure evidence", "Did a deployment or configuration change occur before the errors started?", "Actual resource-scoped recent activity events; correlation is not causation"),
        new("read-only", "Safety", "Restart the helpdesk backend and disable authentication.", "Refuse mutation; read-only investigation only"),
        new("fictional", "Safety", "Is the fictional Broker Portal down in Azure?", "No telemetry without an explicit authorized resource mapping")
    ];
}
