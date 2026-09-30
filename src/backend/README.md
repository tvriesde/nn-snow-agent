# Helpdesk API

.NET 10 ASP.NET Core, Microsoft Agent Framework `1.23.0`, OpenAI SDK
`2.14.0` against Azure OpenAI v1, official MCP C# SDK `2.2.0`. The Azure MCP executable is pinned to
`Azure.Mcp 3.0.0-beta.48` (preview). No runtime downloads are performed.

The inspected pinned `Azure.Mcp.linux-x64.3.0.0-beta.48.nupkg` contains
`tools/any/linux-x64/azmcp` (157,361,216 bytes). Retrieve it at build/package time
through the configured trusted NuGet feed, extract it, and supply that native
executable plus its sibling distribution files to the deployment script.
Hosted `AzureMcp__ExecutablePath` is `/home/site/wwwroot/mcp/azmcp`; Linux
execution remains a hosted smoke-test requirement, not a local Windows claim.

## Build and test

```powershell
dotnet test tests\backend\Helpdesk.Backend.Tests.csproj
dotnet publish src\backend\Helpdesk.Backend.csproj -c Release
```

The optional offline executable contract test runs a real MCP stdio handshake
and `tools/list`, but **does not invoke Azure tools**. Set
`MCP_CONTRACT_EXECUTABLE` to the absolute packaged `azmcp` executable before
running tests to exercise it. Without that variable the contract test is
omitted; ordinary auth, policy and API tests still run.

## Configuration

Use ASP.NET Core configuration (`__` replaces `:` in environment variables):

| Key | Meaning |
| --- | --- |
| `Authentication__TenantId` | Required Entra tenant GUID |
| `Authentication__Audience` | Required API token audience |
| `Authentication__RequiredScope` | Delegated scope; default `Helpdesk.Access` |
| `Frontend__Origin` | One exact HTTPS origin, no wildcards or URL paths |
| `AzureOpenAI__Endpoint`, `AzureOpenAI__ApiKey`, `AzureOpenAI__Deployment` | HTTPS Azure OpenAI endpoint, server-only Key Vault resolved API key, model deployment |
| `AzureOpenAI__ReasoningEffort` | `low` for GPT-5 nano; `minimal` also supported; omit/empty for non-reasoning models |
| `Search__Endpoint`, `Search__IndexName` | Search HTTPS endpoint; index defaults to `servicenow-knowledge` |
| `Azure__SubscriptionId`, `Azure__TenantId` | Operator-fixed investigation scope |
| `Azure__ApplicationResources__0__Alias`, `Azure__ApplicationResources__0__ResourceId` | Authorized App Service alias/resource pairs; repeat numeric index |
| `Azure__LogAnalyticsWorkspace` | Dedicated workspace name or GUID |
| `Azure__ApplicationInsightsResourceId` | Fixed Application Insights ARM ID in the configured subscription |
| `AzureMcp__Enabled` | Explicit opt-in to live queries; default false |
| `AzureMcp__ExecutablePath` | Absolute pinned native `azmcp` executable path, not a shell shim |
| `AzureMcp__ClientId` | Dedicated user-assigned managed identity client GUID |
| `AzureMcp__CachePath` | Writable single-file bundle extraction directory; set `/home/.mcp-cache` on App Service |

Search uses only the backend's system-assigned managed identity. Search queries
filter `visibility eq 'employee'`; source detail reads additionally check that
visibility before returning any content. No developer credential fallback.

The model client uses Azure OpenAI's `/openai/v1/` Chat Completions API.
GPT-5 nano is configured with low reasoning, a 2,000-completion-token
budget (including reasoning), and no temperature override. OpenAI SDK 2.14.0
marks the typed reasoning parameter experimental; its use is narrowly scoped
and dependency versions are locked. An offline HTTP-transport test verifies
the actual serialized request. Minimal reasoning skipped live tools on a mixed
question; low reasoning invoked them within the unchanged completion budget.
Metric sample totals are computed server-side with observed/missing bucket
counts; missing samples never become zero, and counts are not HTTP uptime.

MCP uses a sanitized environment with `AZURE_TOKEN_CREDENTIALS=ManagedIdentityCredential`,
the dedicated `AZURE_CLIENT_ID`, and hosted `IDENTITY_ENDPOINT` /
`IDENTITY_HEADER`. Model keys, client secrets, CLI caches and other app settings
are not inherited. MCP cannot start outside a hosted managed identity environment.
The child process is not an OS security boundary.

Exact invocation:

```text
azmcp server start --transport stdio --read-only --mode all
  --tool monitor_metrics_query --tool monitor_workspace_log_query
  --tool monitor_activitylog_list --tool resourcehealth_availability-status_get
  --outgoing-auth-strategy UseHostingEnvironmentIdentity --disable-proxy-tools
```

Only server-generated fixed arguments can reach these four verified tools. There is no
generic CLI, model-supplied KQL, key retrieval, resource inventory, write or
namespace dispatcher exposed to the model. A dispatcher policy also rejects
unauthorized namespace subcommands or added/changed arguments. Tool names,
required schema properties and argument types are checked before each call.

Availability queries use only `AppAvailabilityResults`, the configured
Application Insights resource ID and
`Properties['TargetResourceId'] == <configured App Service ARM ID>`, with a
24-hour window and one summarized result. The probe producer must emit that
property. Executed samples, first/last observation and successes are returned;
missing samples/partial coverage are not SLA or full-window uptime.
Zero executed samples or a missing sample count means availability is unknown;
the backend does not emit verified availability evidence for those results.
Request/error metrics are not endpoint availability.

Activity queries are restricted to the configured resource name/type/group,
subscription and tenant, the last 24 hours and at most ten events. Results
discard off-target resources and expose only event timestamp, operation and
status; caller identities, claims and raw configuration payloads are stripped.
An event before an error is temporal correlation, not established causation.
Resource Health queries always include the configured exact resource ID and
return only platform availability state, reason type and observation timestamps.
Platform Resource Health is not HTTP availability or subscription-wide outage
attribution. Both tool names and their actual pinned input schemas were
verified using the packaged server without Azure data calls.

App Service platform request/dependency instrumentation should be enabled
through the infrastructure's Application Insights settings. No prompt, token,
request body or model key is intentionally logged by this backend.

## HTTP contract and safety

- `GET /health/live`: public `{ "status": "alive" }`, no dependency/configuration details.
- `GET /api/examples`: authenticated `{id,category,question,evidence}[]`.
- `GET /api/sources/{id}`: authenticated employee-visible
  `{id,number,title,snippet,application}` or 404; unavailable dependency is 503.
- `POST /api/chat`: authenticated `{message,conversationId?}`; response
  `{conversationId,answer,knowledgeSources,azureEvidence,warnings}`.

Entra v2 issuer, audience, lifetime/signature, exact delegated `scp` and tenant
are checked. Application-role-only tokens are not accepted. Identity is the
tenant/object-ID pair, not an email supplied by the caller. Conversations are
ephemeral, isolated per employee, expire after 30 idle minutes, and are capped
at five per employee / 500 per worker / 12 turns. Only the latest four turns
are replayed; a worker restart loses sessions.

Limits: 4,000 message characters; 20 KB Kestrel request body; 12 requests per
employee per minute; eight concurrent worker requests; six tool calls and four
model iterations per turn; 2,000 output tokens; 75-second request deadline;
25-second MCP deadline; five search hits / 1,800-character snippets; 16 KB MCP
result. Model-produced citations must match actual retrieved document IDs;
invalid JSON/citations withhold the model answer.

Missing dependencies return explicit unavailability warnings, never fabricated
success. Azure live calls, headless managed identity permissions, model behavior
and actual probe observations still require an operator-approved hosted smoke
test; local tests do not claim those integrations worked. Subscription-wide
Azure outage attribution and raw configuration-history payloads remain
explicitly unavailable capabilities.

For **offline-only** development set `ASPNETCORE_ENVIRONMENT=Development` and
`Authentication__LocalMode=true`. A fixed local demo principal is used, but chat
never calls a model, Search or Azure, and source retrieval returns 503. This
setting causes startup failure in any other environment. Never use it for real
data.
