using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Helpdesk.Backend;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol.Client;
using Xunit;

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public const string Tenant = "47c94d43-bd0b-4cc0-9c81-412496225c31";
    public const string Audience = "api://test-helpdesk";
    public static readonly SymmetricSecurityKey Key = new(Encoding.UTF8.GetBytes("a-test-only-signing-key-with-at-least-64-characters-never-for-runtime"));
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Authentication:TenantId", Tenant);
        builder.UseSetting("Authentication:Audience", Audience);
        builder.UseSetting("Frontend:Origin", "https://frontend.example");
        builder.ConfigureServices(services =>
        {
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                var metadata = new OpenIdConnectConfiguration { Issuer = $"https://login.microsoftonline.com/{Tenant}/v2.0" };
                metadata.SigningKeys.Add(Key);
                options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(metadata);
                options.TokenValidationParameters.IssuerSigningKey = Key;
            });
        });
    }
    public static string Token(string scope = "Helpdesk.Access", string oid = "employee-one", string? tenant = null,
        string? audience = null, bool expired = false)
    {
        tenant ??= Tenant;
        var token = new JwtSecurityToken($"https://login.microsoftonline.com/{tenant}/v2.0", audience ?? Audience,
            [new("scp", scope), new("oid", oid), new("tid", tenant)], DateTime.UtcNow.AddHours(-2),
            expired ? DateTime.UtcNow.AddHours(-1) : DateTime.UtcNow.AddMinutes(30),
            new SigningCredentials(Key, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
    public HttpClient Employee(string token)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}

public sealed class ApiTests
{
    [Fact]
    public async Task HealthIsPublicAndEmployeeCatalogRequiresAuthentication()
    {
        await using var factory = new ApiFactory();
        var client = factory.CreateClient();
        Assert.Equal("{\"status\":\"alive\"}", await client.GetStringAsync("/health/live"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/examples")).StatusCode);
    }
    [Theory]
    [InlineData("Other.Scope", null, null, false, HttpStatusCode.Forbidden)]
    [InlineData("Helpdesk.Access", "wrong-audience", null, false, HttpStatusCode.Unauthorized)]
    [InlineData("Helpdesk.Access", null, "11111111-1111-1111-1111-111111111111", false, HttpStatusCode.Unauthorized)]
    [InlineData("Helpdesk.Access", null, null, true, HttpStatusCode.Unauthorized)]
    [InlineData("prefixHelpdesk.Access", null, null, false, HttpStatusCode.Forbidden)]
    public async Task JwtChecksIssuerAudienceLifetimeAndExactScope(string scope, string? audience, string? tenant, bool expired, HttpStatusCode expected)
    {
        await using var factory = new ApiFactory();
        var client = factory.Employee(ApiFactory.Token(scope, tenant: tenant, audience: audience, expired: expired));
        Assert.Equal(expected, (await client.GetAsync("/api/examples")).StatusCode);
    }
    [Fact]
    public async Task ContractAndConversationAreIsolatedByEmployee()
    {
        await using var factory = new ApiFactory();
        var first = factory.Employee(ApiFactory.Token());
        var second = factory.Employee(ApiFactory.Token(oid: "employee-two"));
        var catalog = await first.GetFromJsonAsync<ExampleQuestion[]>("/api/examples");
        Assert.Equal(14, catalog!.Length);
        var response = await first.PostAsJsonAsync("/api/chat", new ChatRequest("Is Claims Workbench available?"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ChatResponse>();
        Assert.True(Guid.TryParse(result!.ConversationId, out _));
        Assert.Empty(result.AzureEvidence);
        Assert.Empty(result.KnowledgeSources);
        Assert.Contains("Azure OpenAI is not configured.", result.Warnings);
        Assert.Equal(HttpStatusCode.NotFound,
            (await second.PostAsJsonAsync("/api/chat", new ChatRequest("Continue", result.ConversationId))).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await first.PostAsJsonAsync("/api/chat", new ChatRequest("Continue", result.ConversationId))).StatusCode);
    }
    [Theory]
    [InlineData("", null)]
    [InlineData(" ", null)]
    [InlineData("question", "not-a-guid")]
    public async Task InvalidInputIsRejected(string message, string? conversationId)
    {
        await using var factory = new ApiFactory();
        var client = factory.Employee(ApiFactory.Token());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/chat", new ChatRequest(message, conversationId))).StatusCode);
    }
    [Fact]
    public async Task MessageSizeAndRateLimitsApply()
    {
        await using var factory = new ApiFactory();
        var client = factory.Employee(ApiFactory.Token());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/chat", new ChatRequest(new string('a', 4001)))).StatusCode);
        for (var i = 0; i < 11; i++) Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/examples")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.GetAsync("/api/examples")).StatusCode);
    }
    [Fact]
    public async Task CorsAllowsOnlyExactOrigin()
    {
        await using var factory = new ApiFactory();
        var client = factory.CreateClient();
        foreach (var origin in new[] { "https://frontend.example", "https://frontend.example.evil", "https://evil.example" })
        {
            var request = new HttpRequestMessage(HttpMethod.Options, "/api/chat");
            request.Headers.Add("Origin", origin); request.Headers.Add("Access-Control-Request-Method", "POST");
            var response = await client.SendAsync(request);
            Assert.Equal(origin == "https://frontend.example", response.Headers.Contains("Access-Control-Allow-Origin"));
        }
    }
    [Fact]
    public void ProductionCannotEnableLocalAuthentication()
    {
        using var factory = new ApiFactory();
        using var invalid = factory.WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Production");
            b.UseSetting("Authentication:LocalMode", "true");
        });
        Assert.Throws<InvalidOperationException>(() => invalid.CreateClient());
    }
    [Fact]
    public async Task SourceLookupDoesNotPretendUnavailableDataExists()
    {
        await using var factory = new ApiFactory();
        var client = factory.Employee(ApiFactory.Token());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/api/sources/unknown")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/sources/invalid%27id")).StatusCode);
    }
}

public sealed class PolicyTests
{
    private static IConfiguration Config() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Azure:SubscriptionId"] = "sub", ["Azure:TenantId"] = "tenant",
        ["Azure:ApplicationResources:0:Alias"] = "backend",
        ["Azure:ApplicationResources:0:ResourceId"] = "/subscriptions/sub/resourceGroups/demo/providers/Microsoft.Web/sites/backend",
        ["Azure:LogAnalyticsWorkspace"] = "demo-workspace",
        ["Azure:ApplicationInsightsResourceId"] = "/subscriptions/sub/resourceGroups/demo/providers/microsoft.insights/components/demo"
    }).Build();
    [Fact]
    public void ToolPolicyControlsScopesCommandsArgumentsAndQueries()
    {
        var policy = new AzureToolPolicy(Config());
        Assert.Throws<InvalidOperationException>(() => policy.Build("Broker Portal", "metrics"));
        Assert.Throws<InvalidOperationException>(() => policy.Build("backend", "restart"));
        var request = policy.Build("backend", "metrics");
        Assert.Equal("sub", request.Arguments["subscription"]);
        Assert.Equal(24, request.Arguments["max-buckets"]);
        Assert.True(AzureToolPolicy.ValidateDispatch("monitor", "monitor metrics query", request.Arguments, request.Tool, request.Arguments));
        Assert.False(AzureToolPolicy.ValidateDispatch("monitor", "monitor workspace list", request.Arguments, request.Tool, request.Arguments));
        var attacker = new Dictionary<string, object?>(request.Arguments) { ["subscription"] = "other" };
        Assert.False(AzureToolPolicy.ValidateDispatch(request.Tool, request.Tool, attacker, request.Tool, request.Arguments));
        attacker = new(request.Arguments) { ["command"] = "az keyvault secret show" };
        Assert.False(AzureToolPolicy.ValidateDispatch(request.Tool, request.Tool, attacker, request.Tool, request.Arguments));
        var logs = policy.Build("backend", "availability");
        Assert.Contains("TargetResourceId", logs.Arguments["query"]!.ToString());
        Assert.Equal(1, logs.Arguments["limit"]);
        var activity = policy.Build("backend", "activity");
        Assert.Equal("backend", activity.Arguments["resource-name"]);
        Assert.Equal(10, activity.Arguments["top"]);
        var health = policy.Build("backend", "health");
        Assert.Equal(health.ResourceId, health.Arguments["resourceId"]);
        Assert.True(AzureToolPolicy.ValidateDispatch("resourcehealth", "resourcehealth availability-status get",
            health.Arguments, health.Tool, health.Arguments));
        Assert.False(AzureToolPolicy.ValidateDispatch("resourcehealth", "resourcehealth health-events list",
            health.Arguments, health.Tool, health.Arguments));
    }
    [Fact]
    public void ChildEnvironmentCannotInheritModelKeyOrCachedCredentials()
    {
        var environment = AzureMcpService.ChildEnvironment("dedicated-client");
        Assert.Equal("ManagedIdentityCredential", environment["AZURE_TOKEN_CREDENTIALS"]);
        Assert.Equal("dedicated-client", environment["AZURE_CLIENT_ID"]);
        Assert.DoesNotContain("AzureOpenAI__ApiKey", environment.Keys);
        Assert.DoesNotContain("AZURE_CLIENT_SECRET", environment.Keys);
    }
    [Fact]
    public void CitationsMustMatchActualRetrievedDocuments()
    {
        var ledger = new EvidenceLedger();
        ledger.Knowledge["actual"] = new("actual", "KB001", "Title", "Actual snippet", "Claims");
        var valid = ledger.Finish("conversation", """{"answer":"Try safe steps","knowledgeSourceIds":["actual"]}""");
        Assert.Equal("Actual snippet", Assert.Single(valid.KnowledgeSources).Snippet);
        var invalid = ledger.Finish("conversation", """{"answer":"Fake source","knowledgeSourceIds":["guessed"]}""");
        Assert.Empty(invalid.KnowledgeSources);
        Assert.DoesNotContain("Fake source", invalid.Answer);
        Assert.NotEmpty(invalid.Warnings);
        Assert.Empty(ledger.Finish("conversation", "not JSON").KnowledgeSources);
    }
    [Fact]
    public void ScopeUsesDelegatedExactClaimAndStableTenantObjectIdentity()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity([new("scp", "Other Helpdesk.Access"), new("tid", "t"), new("oid", "u")], "test"));
        Assert.True(AccessPolicy.HasScope(user, "Helpdesk.Access"));
        Assert.Equal("t:u", AccessPolicy.UserKey(user));
        Assert.False(AccessPolicy.HasScope(new ClaimsPrincipal(new ClaimsIdentity([new("roles", "Helpdesk.Access")], "test")), "Helpdesk.Access"));
    }
    [Fact]
    public void ConversationsAreBoundedAndNeverTransferOwnership()
    {
        var store = new ConversationStore(TimeProvider.System);
        var session = store.Get("one", null);
        Assert.Same(session, store.Get("one", session.Id));
        Assert.Throws<ConversationNotFoundException>(() => store.Get("two", session.Id));
        for (var i = 0; i < 4; i++) store.Get("one", null);
        Assert.Throws<ConversationLimitException>(() => store.Get("one", null));
        Assert.NotEqual(session.Id, store.Get("two", null).Id);
    }
    [Fact]
    public void IdleSessionsExpireWithoutReusingTheirState()
    {
        var clock = new TestClock();
        var store = new ConversationStore(clock);
        var session = store.Get("one", null);
        session.History.Add(("question", "answer"));
        clock.Now += TimeSpan.FromMinutes(31);
        Assert.Throws<ConversationNotFoundException>(() => store.Get("one", session.Id));
        Assert.Empty(store.Get("one", null).History);
    }
    [Fact]
    public void SchemaDriftIsFailClosed()
    {
        using var schema = JsonDocument.Parse("""{"properties":{"subscription":{"type":"string"}},"required":["subscription","new-required"]}""");
        Assert.Throws<InvalidOperationException>(() => AzureMcpService.ValidateSchema(schema.RootElement, new() { ["subscription"] = "sub" }));
    }
    [Theory]
    [InlineData("""{"results":[{"executed":0,"succeeded":0}]}""", false)]
    [InlineData("""{"results":[]}""", false)]
    [InlineData("""{"results":[{"executed":2,"succeeded":1}]}""", true)]
    public void NoProbeSamplesNeverMeansPerfectUptime(string json, bool expected)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Equal(expected, AzureMcpService.HasExecutedProbeSamples(document.RootElement));
    }
    [Fact]
    public void ActivityEvidenceStripsCallerAndOtherResourceData()
    {
        using var json = JsonDocument.Parse("""
            {"activityLogs":[
              {"resourceId":"/allowed","eventTimestamp":"2026-09-30T00:00:00Z","operationName":{"value":"Microsoft.Web/sites/write"},"status":{"value":"Succeeded"},"caller":"private@example.test","claims":{"secret":"never expose"}},
              {"resourceId":"/other","eventTimestamp":"2026-09-30T00:00:00Z","operationName":{"value":"unrelated"}}
            ]}
            """);
        var summary = AzureMcpService.SanitizeOperationalEvidence(json.RootElement, "/allowed", "activity");
        Assert.Contains("Microsoft.Web/sites/write", summary);
        Assert.DoesNotContain("private", summary);
        Assert.DoesNotContain("secret", summary);
        Assert.DoesNotContain("unrelated", summary);
    }
    [Fact]
    public void PlatformResourceHealthIsBoundedAndContainsNoConfigurationPayload()
    {
        using var json = JsonDocument.Parse("""{"properties":{"availabilityState":"Available","reasonType":"Unplanned","reportedTime":"2026-09-30T00:00:00Z","summary":"private configuration"}}""");
        var summary = AzureMcpService.SanitizeOperationalEvidence(json.RootElement, "/allowed", "health");
        Assert.Contains("Available", summary);
        Assert.DoesNotContain("private", summary);
    }
    [Fact]
    public async Task ExplicitOfflineModeNeverInvokesRealDependencies()
    {
        var agent = new HelpdeskAgent(Config(), new ForbiddenKnowledge(), new ForbiddenAzure());
        var result = await agent.RunAsync(new Conversation("id", "owner", DateTimeOffset.UtcNow), "question", true, default);
        Assert.Empty(result.AzureEvidence); Assert.Empty(result.KnowledgeSources);
        Assert.Contains("offline", result.Answer);
    }
    private sealed class ForbiddenKnowledge : IKnowledgeService
    {
        public Task<IReadOnlyList<KnowledgeSource>> SearchAsync(string query, CancellationToken cancellationToken) => throw new Xunit.Sdk.XunitException("Real search invoked");
        public Task<KnowledgeSource?> GetAsync(string id, CancellationToken cancellationToken) => throw new Xunit.Sdk.XunitException("Real search invoked");
    }
    private sealed class ForbiddenAzure : IAzureInvestigator
    {
        public Task<AzureEvidence> InvestigateAsync(string alias, string operation, CancellationToken cancellationToken) => throw new Xunit.Sdk.XunitException("Real Azure invoked");
    }
    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
