using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.RateLimiting;
using Helpdesk.Backend;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 20000);
var local = builder.Configuration.GetValue<bool>("Authentication:LocalMode");
if (local && !builder.Environment.IsDevelopment())
    throw new InvalidOperationException("Local offline authentication is permitted only in Development.");
var tenant = builder.Configuration["Authentication:TenantId"];
var audience = builder.Configuration["Authentication:Audience"];
if (!local && (!Guid.TryParse(tenant, out _) || string.IsNullOrWhiteSpace(audience)))
    throw new InvalidOperationException("Entra tenant and API audience are required. Authentication never defaults to anonymous.");
var requiredScope = builder.Configuration["Authentication:RequiredScope"] ?? "Helpdesk.Access";
if (local)
    builder.Services.AddAuthentication("LocalOffline").AddScheme<AuthenticationSchemeOptions, LocalOfflineHandler>("LocalOffline", _ => { });
else
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
    {
        o.Authority = $"https://login.microsoftonline.com/{tenant}/v2.0";
        o.Audience = audience;
        o.RequireHttpsMetadata = true;
        o.MapInboundClaims = false;
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true, ValidIssuer = $"https://login.microsoftonline.com/{tenant}/v2.0",
            ValidateAudience = true, ValidAudience = audience, ValidateLifetime = true, ValidateIssuerSigningKey = true,
            ClockSkew = TimeSpan.FromMinutes(1)
        };
    });
builder.Services.AddAuthorization(o => o.AddPolicy("Employee", p => p.RequireAuthenticatedUser()
    .RequireAssertion(c => AccessPolicy.HasScope(c.User, requiredScope) && AccessPolicy.UserKey(c.User) is not null &&
        (local || c.User.FindFirstValue("tid") == tenant))));
var origin = builder.Configuration["Frontend:Origin"];
if (!string.IsNullOrWhiteSpace(origin) &&
    (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri) || originUri.AbsolutePath != "/" ||
     originUri.Query != "" || originUri.Fragment != "" || origin.Contains('*') ||
     (originUri.Scheme != "https" && !(builder.Environment.IsDevelopment() && originUri.IsLoopback))))
    throw new InvalidOperationException("Frontend origin must be one exact HTTPS origin.");
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
{
    if (!string.IsNullOrWhiteSpace(origin)) p.WithOrigins(origin.TrimEnd('/')).WithMethods("GET", "POST").WithHeaders("Authorization", "Content-Type");
}));
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("EmployeeRate", context => RateLimitPartition.GetFixedWindowLimiter(
        AccessPolicy.UserKey(context.User) ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 12, Window = TimeSpan.FromMinutes(1), QueueLimit = 0,
            AutoReplenishment = true
        }));
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetConcurrencyLimiter("worker", _ => new ConcurrencyLimiterOptions { PermitLimit = 8, QueueLimit = 0 }));
});
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ConversationStore>();
builder.Services.AddSingleton<IKnowledgeService, KnowledgeService>();
builder.Services.AddSingleton<AzureToolPolicy>();
builder.Services.AddSingleton<AzureMcpService>();
builder.Services.AddSingleton<IAzureInvestigator>(s => s.GetRequiredService<AzureMcpService>());
builder.Services.AddSingleton<IHealthModelMcp>(s => s.GetRequiredService<AzureMcpService>());
builder.Services.AddSingleton<ApplicationHealthSkill>();
builder.Services.AddSingleton<IHelpdeskModelClientFactory, HelpdeskModelClientFactory>();
builder.Services.AddSingleton<ModelCatalog>();
builder.Services.AddTransient<HelpdeskAgent>();
var app = builder.Build();
_ = app.Services.GetRequiredService<ApplicationHealthSkill>();
_ = app.Services.GetRequiredService<ModelCatalog>();
app.UseExceptionHandler(handler => handler.Run(async context =>
{
    context.Response.StatusCode = 500;
    await context.Response.WriteAsJsonAsync(new { error = "The request could not be completed." });
}));
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapGet("/health/live", () => Results.Ok(new { status = "alive" })).AllowAnonymous();
var api = app.MapGroup("/api").RequireAuthorization("Employee").RequireRateLimiting("EmployeeRate");
api.MapGet("/examples", () => Results.Ok(Examples.Catalog));
api.MapGet("/models", (ModelCatalog models) => Results.Ok(models.PublicCatalog()));
api.MapGet("/sources/{id}", async (string id, IKnowledgeService knowledge, CancellationToken ct) =>
{
    if (local) return Results.Problem("Knowledge retrieval is unavailable in explicit local offline mode.", statusCode: 503);
    if (id.Length > 200 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-' or '='))) return Results.BadRequest();
    try
    {
        var source = await knowledge.GetAsync(id, ct);
        return source is null ? Results.NotFound() : Results.Ok(source);
    }
    catch (Exception) when (!ct.IsCancellationRequested) { return Results.Problem("Knowledge retrieval is unavailable.", statusCode: 503); }
});
api.MapPost("/chat", async (ChatRequest request, HttpContext context, ConversationStore store, HelpdeskAgent agent, ModelCatalog models) =>
{
    if (!AccessPolicy.ValidRequest(request)) return Results.BadRequest(new { error = "Message must contain 1–4000 characters; conversationId must be a UUID." });
    if (context.Request.ContentLength > 20000) return Results.StatusCode(413);
    if (!models.TryResolve(request.ModelId, out _))
        return Results.BadRequest(new { error = "The requested model is not available. Choose a model from the model catalog." });
    Conversation conversation;
    try { conversation = store.Get(AccessPolicy.UserKey(context.User)!, request.ConversationId); }
    catch (ConversationNotFoundException) { return Results.NotFound(new { error = "Conversation not found or expired." }); }
    catch (ConversationLimitException) { return Results.Problem("Conversation capacity reached.", statusCode: 429); }
    if (!await conversation.Gate.WaitAsync(0, context.RequestAborted)) return Results.StatusCode(409);
    try
    {
        if (conversation.History.Count >= 12) return Results.Problem("Conversation turn limit reached. Start a new conversation.", statusCode: 409);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        timeout.CancelAfter(TimeSpan.FromSeconds(75));
        var response = await agent.RunAsync(conversation, request.Message, local, timeout.Token, request.ModelId);
        conversation.History.Add((request.Message, response.Answer));
        return Results.Ok(response);
    }
    catch (OperationCanceledException) { return Results.Problem("The request timed out or was cancelled.", statusCode: 504); }
    finally { conversation.Gate.Release(); }
});
app.Run();

public partial class Program;
public sealed class LocalOfflineHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // A fixed explicit demo principal cannot select an employee identity or access real dependencies.
        var identity = new ClaimsIdentity(new[]
        {
            new Claim("tid", "offline"), new Claim("oid", "local-demo"), new Claim("scp", "Helpdesk.Access")
        }, Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}
