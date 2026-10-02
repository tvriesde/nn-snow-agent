# Insurance IT Helpdesk Agent

An internal employee helpdesk proof of concept grounded in synthetic
ServiceNow knowledge and read-only live Azure evidence.

**The approved demo is deployed and live acceptance checks passed.** Hosting,
employee sign-in, indexed knowledge answers, scoped Azure MCP health/metrics,
and mixed knowledge/live answers are verified. Measured HTTP uptime remains
unknown with optional availability probes disabled. The
selected subscription is `d860292c-5d2c-4df3-b7c8-332bd46882d1`, and Sweden
Central is the preferred region wherever the selected services are supported.
See the [deployment plan](.azure/deployment-plan.md) for decisions and limits.

## Architecture

```text
Entra-authenticated employee
  -> Expo / React Native Web frontend on App Service
  -> ASP.NET Core / Microsoft Agent Framework backend on App Service
       -> Azure OpenAI GPT-5 nano / GPT-6 luna (server-side API key)
       -> Azure AI Search (employee-visible knowledge only)
       -> official Azure MCP Server (managed identity; read-only tools)

Synthetic ServiceNow API responses in private Blob Storage
  -> scheduled .NET isolated Azure Function
  -> incremental Search upserts / deletions

Application Insights / Log Analytics
  -> timestamped live Azure evidence, not simulated uptime
```

React Native Web is the browser target. This project does not deliver an
iOS/Android app-store binary.

### Model selection and answer processing

The chat model selector chooses **GPT-5 nano** (default, `helpdesk-mini`,
`2025-08-07`) or **GPT-6 luna** (`helpdesk-luna`, `2026-09-22`) for the next
question without clearing the conversation. Both use EU DataZoneStandard
in Sweden Central; the new Luna deployment has capacity 10. Standalone
application-health checks still run the MCP-only skill with no model calls,
regardless of the selection.

Authenticated `GET /api/models` returns only configured IDs and display labels.
`POST /api/chat` accepts optional `modelId`; omission uses the configured
default, and unknown IDs are rejected before creating a conversation.
Configure `AzureOpenAI:Models:<index>:{Id,Label,Deployment,ReasoningEffort,Api}` and
`AzureOpenAI:DefaultModelId`. Existing single-deployment configuration remains
supported. Deployment names, endpoints and credentials cannot be supplied
by employees. The dedicated [additional-model template](infra/additional-model.bicep)
adds Luna to an existing account without redeploying other resources.
Nano retains Chat Completions (`Api=chatCompletions`, also the legacy default);
Luna uses Responses (`Api=responses`) because its live Chat Completions API
rejects function tools with low reasoning. Both retain low reasoning and the
2,000-output-token per-round budget. Luna explicitly disables provider response
storage; conversation context remains in the existing application-owned store.

Every answer shows elapsed server-agent time (excluding browser/network time),
selected model, actual provider model when reported, and inference-call count.
Token totals sum every model round only if all rounds report consistent complete
usage. Cached input and reasoning are included subsets; missing counts are not
zero. Optional `Pricing` configuration requires verified USD rates per million,
`MaximumInputTokens`, HTTPS `Source` and `AsOf` date. The estimate excludes
Search, MCP, hosting, tax and discounts. Luna estimates are withheld because
its cache-write billing counts/context-tier applicability are not fully verified.
Unavailable usage/cost is explained, never fabricated.

The backend uses pinned Microsoft Agent Framework and official MCP SDK
packages. The Azure MCP executable is pinned to `3.0.0-beta.49`, a beta
release published on 2026-10-01. The official Linux x64 archive and executable
are SHA-256-pinned in `scripts/common.ps1`; the trusted NuGet fallback must
match the same executable hash. Offline stdio/allowlist/schema contract tests
passed against the matching Windows x64 release on 2026-10-02 without Azure
calls. Linux hosted-identity/live tool behavior must be reverified at deployment.
Treat the beta as a demo dependency, not production certification.
Official release: https://github.com/microsoft/mcp/releases/tag/Azure.Mcp.Server-3.0.0-beta.49

### Evaluated application health skill

The backend publishes the exact
[azure-health-model-state skill](.github/skills/azure-health-model-state/SKILL.md)
as `skills/azure-health-model-state/SKILL.md`. Startup loads its source rules,
resolution workflow and reporting instructions; editor setup and historical
examples are excluded from the agent prompt. Application-health questions use
`GetApplicationHealth`, not infrastructure `InvestigateAzure`.

The skill uses only the packaged Azure MCP `monitor_healthmodels_list` and
`monitor_healthmodels_get`, with fresh schema-validated calls under the dedicated
managed identity. The native allowlist now contains six read-only commands.
Only `results.healthModel.healthState` determines Healthy, Degraded, Unhealthy
or Unknown. Provisioning success, metrics, missing alerts and platform Resource
Health cannot establish evaluated application health. The report is server-owned
and cannot be overwritten by model-generated text.

Hosted adaptation keeps all lookups in `Azure:SubscriptionId`, rather than
enumerating employees' subscriptions. `Azure:HealthApplication` is the exact
application-tag value for "this helpdesk" (`employee-it-helpdesk` in this demo).
Applications/custom tags match exactly after trimming, case-insensitively;
model names/resource IDs also require exact matches. Multiple matches request
selection and name-only candidates are unconfirmed, without reporting their
health. Missing models, inaccessible commands or missing evaluated state
produce explicit Unknown/evidence-gap reports, never another application's
health. Discovery is bounded to 20 models and 45 seconds; specify a model and
resource group for larger subscriptions. No CLI/REST fallback, cached health,
entity guessing, health-model mutation or added RBAC is used.

Health answers now lead with a plain-language state/meaning, UTC check time
and any evidence gap. Technical model/tag/subscription/command/provisioning
details remain in expandable Azure evidence. The locally verified beta.49
MCP command set has no component-health or failing-signal command: Degraded
cannot identify a contributing service or root cause. The report explicitly
states this limitation rather than attributing degradation to an arbitrary
service. All health lookups remain MCP-only.

Unambiguous standalone questions such as "Is this IT helpdesk application
healthy right now?" route directly to the packaged skill: no Search prefetch
and no OpenAI/tool-loop generation rounds. Mixed questions, unclear names,
model-name queries and context-dependent follow-ups retain normal agent
routing. Every check still freshly lists/resolves/gets its model, so new
ambiguity is not hidden by a resolution cache. The MCP process/connection
remains reusable. Skill duration and MCP call count are logged for measurement;
Azure response time and F1 cold starts remain. No paid hosting or inferred
health cache is introduced.

## Project layout

| Path | Purpose |
| --- | --- |
| [src/frontend](src/frontend) | Employee chat, example questions, Entra sign-in |
| [src/backend](src/backend) | Agent API, Search grounding, read-only MCP integration |
| [src/core](src/core) | Source validation, safe projection, delta engine, index schema |
| [src/indexer](src/indexer) | Timer Functions, Blob lease/checkpoints, Search writes |
| [data/servicenow](data/servicenow) | Deterministically generated synthetic fixtures |
| [infra](infra) | Bicep infrastructure and identities |
| [scripts](scripts) | Deployment, deletion, fixture generation, delta publication |
| [tests](tests) | Backend and indexing tests |

## Prerequisites

- .NET 10 SDK.
- Node.js 24 LTS and npm.
- PowerShell 7.4 or newer.
- Azure CLI and Bicep for infrastructure validation/deployment.
- Optional Azure Functions Core Tools v4 and Azurite for running timer Functions
  locally.
- Azure provisioning and role-assignment permissions for deployment.
- Permission to create the two dedicated Entra app registrations, or existing
  frontend/API registration IDs.
- A compatible GPT deployment quota and an available Azure AI Search Free slot.

The repository never requires real ServiceNow credentials. Do not commit local
settings, API keys, access tokens, or deployment ownership manifests.

## Build and test

From the repository root:

```powershell
dotnet build .\Insurance.Helpdesk.slnx --configuration Release
dotnet test .\Insurance.Helpdesk.slnx --configuration Release
```

The indexing tests do not require Azure. They validate committed fixture hashes,
227 source records, 176 employee-facing documents, no-op indexing, same-timestamp
updates, deletes/unpublication, shrinking chunks, partial failures, replay, and
checkpoint safety.

Frontend validation and local development:

```powershell
Set-Location .\src\frontend
npm ci
npm run typecheck
npm test
npm run build
npm run dev
```

For production-style local serving, use `npm start` after building. The Node
server uses `PORT` (default 8080) and serves public configuration from
`API_BASE_URL`, `ENTRA_TENANT_ID`, `ENTRA_CLIENT_ID`, and `ENTRA_API_SCOPE`.
The frontend refuses sign-in/chat when those settings are missing. The SPA
registration must include the exact frontend root redirect URL.

The employee interface follows nn.nl's visual language: a sand-coloured portal
bar, a white header with the official Nationale-Nederlanden logo (used with
NN's authorization), orange hero headings in a white card, dark-grey 4px
buttons, softly shadowed tiles with orange arrows, and a sand footer. The logo
is bundled as `public/nn-logo.svg`; `public/nn-logo-dark.svg` only switches the
wordmark to white for dark mode. NN's proprietary Nitti Grotesk fonts and NN
photography are not bundled; Helvetica/Arial is used instead. The interface
remains explicitly labeled as a demo, not an official NN service.
Light/dark themes use shared CSS tokens; small orange text uses a darker
orange for readable contrast. Sign-in, chat, evidence inspection, and example
filtering retain their existing behavior.
The **Architecture & agent** page at `/architecture` includes a theme-aware
architecture diagram with an accessible description and an editable Excalidraw
download. The diagram shows the authenticated request, model/key boundary,
knowledge retrieval, read-only MCP evidence and scheduled indexing paths.
Small screens can scroll the diagram within its panel, without widening the
page. The page also explains the request
and delta-indexing flows, model/tool budgets, allowed live aliases, identity
roles and safety limits. It is a public design/configuration reference, not
a live inventory; it includes no keys or deployment identity/resource IDs.

Azure-connected chat validation additionally requires model, Search, identities, and employee
authentication configuration; a successful local build is not a claim that live
Azure grounding has been verified.
Each model/tool round uses a strict JSON response schema bound to the
server's current retrieved citation IDs. Additional tool retrieval refreshes
that schema. The server independently validates returned IDs before exposing
an answer; missing evidence never becomes a fabricated citation.
Retrieved evidence is placed before the current employee request and remains
untrusted data, never a system instruction. Clear live-evidence requests use
the scoped read-only tools without an unnecessary second confirmation.

## Synthetic ServiceNow data

The [fixture generator](scripts/generate-demo-data.ps1) creates Table API-shaped
`result` envelopes with:

- 60 knowledge articles.
- 120 incidents, including 96 resolved cases.
- 20 known-problem records.
- 15 change records.
- 12 business-application CMDB records.

Applications include Claims Workbench, Policy Administration, Broker Portal,
Underwriting Desktop, Document Vault, Actuarial Analytics, Finance Reporting,
and Employee IT Helpdesk.

```powershell
.\scripts\generate-demo-data.ps1
```

Existing fixtures are not replaced unless `-Force` is explicitly supplied.
Regenerating local fixtures never resets a deployed index or checkpoint.

Only published, active employee KBs, sanitized resolved-incident close notes,
and known-problem resolutions are indexed. Closed incidents can be inactive,
as in typical ServiceNow responses. Internal work notes, caller details, open
incident drafts, raw change records, and CMDB inventories are not employee
troubleshooting evidence. Reference objects include synthetic display values
and `.invalid` URLs; they must not be mistaken for a real ServiceNow instance.

The business applications are fictional. Only the deployed helpdesk
frontend/backend are automatically mapped to live Azure resources. The agent
must say that live evidence is unavailable for an unmapped fictional app.

## Scheduled delta indexing

The Function runs every 15 minutes by default. Azure AI Search is a destination;
the Function, not a separate Search indexer, controls the schedule.

```text
manifest.json -> committed, contiguous batch sequences and SHA-256 hashes
baseline/*.json -> initial Table API-shaped snapshots
changes/*.json -> immutable synthetic upsert/delete batches
indexer-state/checkpoint.json -> leased durable cursor
indexer-state/documents/<table>/<sys_id>.json -> document version/chunk IDs
```

The producer publishes payloads before atomically replacing the manifest.
The indexer processes only committed batches, under a renewable checkpoint
lease. It advances the cursor only after all Search items and record states
are committed. Retries are at-least-once and idempotent. Equal timestamps do
not lose changes because the authoritative order is the committed sequence.

Unpublication and audience restriction delete previously searchable chunks.
Article shrinkage removes obsolete chunks. Failed items retain the prior
checkpoint and are replayed. A no-change tick performs no Search writes.

Source changes, including unpublication or visibility revocation, take effect
after a successful scheduled sync, not immediately. This synthetic demo's
eventually consistent index is not a replacement for real-time authorization
on sensitive production ServiceNow records.

### Demonstrate a change

Create a local delta without changing Azure:

```powershell
.\scripts\publish-demo-delta.ps1 -Scenario SameTimestamp
```

Or publish directly to the deployed source container after deployment:

```powershell
.\scripts\publish-demo-delta.ps1 `
  -Scenario Update `
  -Upload `
  -StorageAccountName '<deployed-storage-account>' `
  -SubscriptionId 'd860292c-5d2c-4df3-b7c8-332bd46882d1'
```

Supported scenarios: `Insert`, `Update`, `Delete`, `Unpublish`, `Shrink`, and
`SameTimestamp`. Remote publication uses a Storage token, immutable batch
uploads, and an ETag-protected manifest commit. A conflicting producer leaves
an uncommitted batch that the indexer ignores; retry using the latest manifest.
Do not manually overwrite immutable published batches.

For an isolated local demonstration, generate fixtures into another directory
and pass that directory to the producer rather than modifying the committed
baseline.

## Indexer local configuration

Copy [local.settings.example.json](src/indexer/local.settings.example.json) to
`src/indexer/local.settings.json`, which is ignored by Git. Local timer
coordination uses Azurite; source and Search still require configured services.
The explicit `Authentication__UseDeveloperCredential=true` local option uses
your Azure CLI login. Do not set this in deployed applications: deployment uses
managed identity.

Worker telemetry is explicitly bound to the same configured identity for
Entra-authenticated ingestion; the infrastructure disables local-key telemetry
authentication and grants only the telemetry publisher permission needed.

| Setting | Meaning |
| --- | --- |
| `Source__BlobServiceUri` | Private source Storage account Blob endpoint |
| `Source__ContainerName` | Source container, default `servicenow` |
| `State__ContainerName` | Lease/checkpoint container, default `indexer-state` |
| `Search__Endpoint` | Azure AI Search endpoint |
| `Search__IndexName` | Default `servicenow-knowledge` |
| `IndexingSchedule` | Default `0 */15 * * * *`; flat key for Functions timer binding resolution |
| `Indexing__MaxBatches` | Default 5, maximum 50 per invocation |

Use source batches of at most 1,000 records and 4 MB. Real ServiceNow
incremental API polling would require a separately implemented producer;
the delta envelopes are explicit simulator metadata, not native Table API
delete events.

## Authentication and read-only Azure access

Employees authenticate through single-tenant Entra ID using PKCE. The backend
validates tenant/issuer, API audience, and the `Helpdesk.Access` scope.
The browser never receives the model API key.

- Backend: Search Index Data Reader and access to its model secret.
- Azure MCP: dedicated user-assigned identity, subscription Reader, and
  workspace-scoped log reading where required.
- Indexer: separate Search document-writing and source/state/host Storage roles.
- Deployment operator: provisioning permissions, never reused as runtime auth.

Read-only mode is reinforced by exact tool/command restrictions, fixed
subscription/workspace/resource scopes, argument validation, and no generic
CLI execution. No agent tool can restart an app, change auth, create resources,
retrieve keys, or edit infrastructure.

The MCP child process is not a hard OS isolation boundary. Keep its environment
free of model secrets and validate identity/tool behavior during deployed
acceptance testing.

## Availability and cost caveats

Free F1 App Service is intentionally a demo configuration: daily CPU quotas,
idle shutdown, cold starts, and no availability SLA. AI Search Free has a
50 MB service limit and allows only one Free service per subscription.
Do not silently upgrade either tier if provisioning fails.

GPT tokens, Function consumption beyond subscription grants, Blob operations,
Key Vault operations, telemetry, and network egress can cost money. Use
pay-per-token model deployment rather than provisioned throughput; keep
retrieval and model outputs bounded. The baseline retrieval uses ordinary
keyword Search without an additional embedding model or paid semantic ranker.
The approved GPT-5 nano DataZoneStandard allocation defaults to capacity 50
(50 requests / 50,000 tokens per minute in the selected region). This allocates
quota, not provisioned throughput; billing remains per token. Capacity 1 only
allows 1,000 tokens per minute, below the grounded prompt plus response budget,
and caused repeated 429 responses. Lower allocations can throttle tool calls
and multi-turn conversations.

Optional Function availability probes write real, timestamped observations.
They can wake F1 apps and consume its quota, so they are not an Always On
workaround. Probe success rate is sampled single-location availability,
not contractual uptime or a guarantee of successful employee login.
Resource state and request success rate must not be mislabeled as uptime.
Fresh deployments have limited history; missing evidence must be reported.

## Deployment and deletion

Review the [deployment plan](.azure/deployment-plan.md), Bicep what-if, selected
model/SKU availability, and script help before deployment. Creating resources,
Entra registrations, and subscription Reader assignments requires explicit
deployment approval. Exact command options are documented by each script:

```powershell
Get-Help .\scripts\deploy.ps1 -Detailed
Get-Help .\scripts\delete.ps1 -Detailed
```

Example for a **separately approved first deployment**, run from the repository
root:

```powershell
$provider = {
    param($apiId, $spaId, $tenant, $url)
    Read-Host "Delegated Helpdesk.Access token after sign-in at $url" -MaskInput
}

.\scripts\deploy.ps1 `
    -SubscriptionId 'd860292c-5d2c-4df3-b7c8-332bd46882d1' `
    -TenantId '47c94d43-bd0b-4cc0-9c81-412496225c31' `
    -Location 'swedencentral' `
    -CreateEntraApplications `
    -DownloadPinnedMcp `
    -SeedDemoData `
    -ModelName 'gpt-5-nano' `
    -ModelVersion '2025-08-07' `
    -ModelSku DataZoneStandard `
    -HealthTokenProvider $provider `
    -Confirm
```

The provider runs after publishing. Obtain the delegated employee API token
through the deployed frontend sign-in, and enter it only in your local masked
terminal prompt, never in chat or a committed file. The script uses the token
for an authenticated API check; this check alone does not prove that GPT or
hosted MCP calls work. Test both knowledge-grounded and live-Azure questions in
the signed-in frontend before considering the deployed flow accepted.

The MCP binary is restored automatically with a pinned checksum/architecture
check; a manual executable override is optional. See [infrastructure
instructions](infra/README.md) for token acquisition, exact parameters, and
operator permissions. Omit `-SeedDemoData` on deployment reruns: existing
source/checkpoints are preserved.

GPT-5 nano uses the Azure OpenAI v1 endpoint and low reasoning effort with a
bounded completion-token budget. Reasoning tokens are billed; low can consume
more tokens than minimal, within the unchanged 2,000-token per-round cap.
EU DataZoneStandard processing was approved:
the resource is in Sweden Central, while inference can occur elsewhere within
the EU. The previously considered gpt-4o-mini version was rejected by actual
Azure preflight as retired and is not the deployment default.

Deletion must use the recorded ownership manifest and `-WhatIf` first.
Remove the subscription-scoped MCP Reader assignment before deleting the
owned resource group. Directory app registrations need separate, explicit
cleanup approval. Never delete pre-existing resources or purge Key Vault by
default.

```powershell
.\scripts\delete.ps1 -WhatIf
$owner = (Get-Content .azure\ownership.json -Raw | ConvertFrom-Json).ownershipId
.\scripts\delete.ps1 `
    -DeleteOwnedEntraApplications `
    -EntraDeletionConfirmation $owner `
    -Confirm
```

Omitting the Entra-deletion options preserves those registrations.

## Acceptance checklist

- Unauthenticated employee API access fails with 401/403.
- KB answers cite only retrieved employee-visible knowledge.
- Azure questions call real allowed MCP tools and label time/resource scope.
- Mixed answers distinguish synthetic incident history from live Azure state.
- Write requests are refused; developer/operator credentials are not used in Azure.
- Deltas alter only affected records, and failure never advances the cursor early.
- Search storage usage is measured after indexing, not inferred from fixture size.
- No-evidence and dependency failures produce visible errors, not fake answers.
- Deploy reruns preserve source/checkpoint state; deletion honors ownership.

Live checks on 2026-09-30 verified knowledge citations, authenticated source
inspection, real Resource Health and metric evidence, mixed answers, refusal
of writes, and no fabricated telemetry for unmapped applications. Scheduled
no-change indexing completed successfully at 13:00 and 13:15 UTC. Search was
measured at 176 documents / 208,922 bytes, with checkpoint and manifest at
sequence 5 and the lease released. These are demo acceptance results, not a
production reliability or uptime certification. Future deployments still
require fresh checks.
