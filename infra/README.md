# Owned, minimum-cost demo deployment

These templates and scripts provision the approved demo. Live checks verified
hosting, sign-in, indexing, grounded model answers, scoped MCP health/metrics,
and mixed answers. Optional probes remain disabled, so measured HTTP uptime is
unknown. Bicep compilation and local PowerShell safety tests alone do not
verify live deployment or regional capacity. New deployments require an
explicitly approved execution of `deploy.ps1`.

## Resources and security

### Local evaluation deployments

`eval-models.bicep` adds `eval-gpt-5-mini`, `eval-gpt-4-1-mini`, `eval-gpt-4-1`
and the separate GPT-5 `eval-judge` to the existing OpenAI account. All four
are pinned, EU DataZoneStandard, capacity 10, without automatic upgrades.
Two explicitly approved operator roles grant Search Index Data Reader on
the demo Search service and Key Vault Secrets User scoped only to its
`azure-openai-key` secret. No backend/frontend setting, existing model,
index document or application identity is changed.

`scripts/deploy-eval-models.ps1` defaults to ARM validation and an
additive-only what-if; `-Deploy` requires the validated deployment plan.
It rejects ownership/context mismatches and non-additive changes. See
[local evaluation setup and results](../evals/README.md).

### Hosted application resources

`additional-model.bicep` deploys only GPT-6 luna `2026-09-22` as
`helpdesk-luna` on an existing Azure OpenAI account, EU `DataZoneStandard`,
capacity 10, with automatic upgrades disabled. It does not change the existing
GPT-5 nano deployment, identities, Search or indexer. The full template also
includes this module (`lunaModelEnabled`, default true; `lunaModelCapacity`,
default 10) and configures both models in the backend's allowlisted catalog,
with GPT-5 nano remaining the default.
The luna catalog entry sets `Api=responses` to support tools with low reasoning;
nano retains Chat Completions. Responses explicitly disables provider storage.

The catalog's pricing uses official Azure Retail API USD Data Zone rates checked
2026-10-02. Nano input/cached input/output rates are 0.055/0.0055/0.44 per million.
Luna short-context rates are 0.12/0.012/0.60, plus 0.15 for cache writes.
Estimates require complete per-round usage and cached-input counts. A conservative
20,000-input-token per-call applicability bound is an application estimate guard,
not a claimed provider tier threshold. Luna estimates remain unavailable because
the verified SDK usage contract has no cache-write billing count. Other Azure
costs, tax and negotiated discounts are excluded; these prices are not a budget.

`main.bicep` is subscription-scoped: it creates a tagged resource group, calls
the resource-group module, and grants subscription Reader to a dedicated Azure
MCP identity. A workspace-scoped Log Analytics Reader grant permits querying
this demo's workspace; the identity has no write grants. Backend and frontend
share a Linux F1 plan. No paid hosting fallback is implemented.

The independently deployable subscription-scoped `health-model.bicep` module
targets an existing resource group and is also called by `main.bicep`.
Its resource-group child module, `modules/health-model-resources.bicep`, creates
a `Microsoft.CloudHealth/healthmodels@2026-09-01-preview` resource named
`<namePrefix>-health` (default `snowdemo-health`), following the existing
`<namePrefix>-<purpose>` naming convention. It carries the same application and
ownership tags, uses a system-assigned identity, and creates the `systemassigned`
ManagedIdentity authentication setting. The subscription module grants only
Reader (`acdd72a7-3385-48ef-bd42-f606fba81ae7`) to that identity at subscription
scope, with a deterministic role assignment name tied to the resource and ownership ID.
The module outputs its name, resource ID, principal ID, authentication setting ID
and Reader assignment ID. Full deployment records the health model and its grant
in the ownership manifest.

The module preserves the exported health-model content using the original entity,
signal and relationship IDs, canvas positions, impact and metric evaluation rules.
Entities and relationships use the export's `2026-05-01-preview` API; the model
and authentication retain their existing `2026-09-01-preview` API.

- Root model -> frontend -> backend, plus frontend/backend -> shared F1 plan
  `IsHostedWithin` relationships.
- Backend/frontend HTTP 5xx: Maximum over `PT5M`, degraded and unhealthy when
  greater than 0, refreshed every `PT1M` (matching the export exactly).
- Plan CPU: Average over `PT5M`, degraded above 80%, unhealthy above 95%.
- Plan memory: Average over `PT1M`, degraded above 75%, unhealthy above 90%.
  Both plan signals refresh every `PT1M`.

`backendResourceId`, `frontendResourceId` and `appServicePlanResourceId` are
parameterized, with standalone defaults derived from the target subscription,
resource group and existing resource naming convention. The main deployment
passes the actual resource IDs from the app module, ensuring those resources are
created before model content is deployed. Display names are derived from these
IDs; no subscription, resource suffix or ownership value is hardcoded.

Redeployment reapplies this version-controlled content to the same IDs.
Later portal edits must also be captured in the module to avoid overwriting
those managed properties. Resources outside the supplied export are not managed
or deleted by this incremental deployment. Backend health-tool integration
remains a separate step; metric health depends on live data being available.
CloudHealth provider registration and regional availability must be checked
before deployment; the deployment operator also needs subscription-level role
assignment permission.

Run `pwsh -File infra\tests\health-model-contract.ps1` to compile both deployment
entry points and check the model, authentication, subscription RBAC, output wiring
and cleanup safety using local mocks. The test never deploys or deletes Azure resources.

The indexer uses **system-assigned identity**, Flex FC1, the discovered
`dotnet-isolated` .NET 10 runtime, 512 MB and no always-ready instances.
The catalog's display version is `10`, but its required ARM
`functionAppConfig.runtime.version` is `10.0`; deployment checks both.
Both timer bindings use the flat `IndexingSchedule` setting. Double-underscore
environment names are normalized to configuration sections, so they must not
be used literally in a timer's `%setting%` placeholder.
Indexing groups Search actions per committed source batch rather than issuing
one request per record. Record versions are persisted only after all upserts
and deletions succeed; partial failures remain safely replayable.
Its identity has Storage Blob Data Owner, Storage Queue Data Contributor, and
Storage Account Contributor on the dedicated storage account for identity-based
Function host storage, plus Search Index Data Contributor. Source/state access
uses this same identity. No storage keys, connection strings, public containers,
VNet or private endpoints are introduced. Public service endpoints are
authenticated; "private storage" means private containers, not a private network.

Backend system identity reads Search documents and Key Vault secrets and writes
authenticated telemetry. The dedicated MCP UAMI is attached only to the backend.
Application Insights disables local authentication; the two .NET identities have
Monitoring Metrics Publisher. Log Analytics retention is 30 days with a 0.1 GB/day
ingestion cap (not a guarantee that billing stops at an exact amount).
Backend request/dependency telemetry uses App Service automatic instrumentation
(`ApplicationInsightsAgent_EXTENSION_VERSION=~3`); profiler and snapshot features
are disabled. No prompt, body or secret capture is enabled by this infrastructure.

Function composition sources discovered via the official template manifest:

- <https://cdn.functions.azure.com/public/templates-manifest/manifest.json>
- `Azure-Samples/functions-quickstart-dotnet-azd-timer`, `main`,
  `infra/app/api.bicep` and `infra/app/rbac.bicep`
- `Azure-Samples/functions-quickstart-dotnet-azd`, `v1.0.0`,
  `infra/app/api.bicep`

These preserve Flex deployment storage, managed-identity authentication,
runtime/scale configuration and required roles, while selecting the templates'
supported system identity alternative and omitting optional VNet infrastructure.
No templates are cloned over existing application code.

## Prerequisites

PowerShell 7.2+, Azure CLI with Bicep, .NET 10 SDK, Node 24/npm, and an authenticated
Azure context in subscription `d860292c-5d2c-4df3-b7c8-332bd46882d1`,
tenant `47c94d43-bd0b-4cc0-9c81-412496225c31`. Scripts do not log in or switch
subscriptions. The operator needs resource provisioning and scoped role assignment
permissions, OpenAI key-list permission, and (only if opted in) permission to
create/configure Entra registrations. Provider registration, quota increases,
admin consent and paid SKU changes are not performed automatically.

Unless `-McpExecutablePath` is supplied, deployment automatically downloads the verified official
**Linux x64** Azure MCP `3.0.0-beta.48` release ZIP during deployment preparation,
verifies its fixed SHA256, extracts it and validates the x64 ELF header. Alternatively,
pass an already downloaded distribution's executable with `-McpExecutablePath`;
all sibling runtime files are packaged. Do not pass a Windows tool installation.
The packaged filename becomes `mcp/azmcp`, and the backend startup command restores
its executable bit. Neither option downloads anything at application startup.
`-DownloadPinnedMcp` is an optional explicit spelling of the default automatic
mode; it cannot be combined with `-McpExecutablePath`. There is no version/platform
fallback.

Pinned artifact:
<https://github.com/microsoft/mcp/releases/download/Azure.Mcp.Server-3.0.0-beta.48/Azure.Mcp.Server-linux-x64.zip>

SHA256: `dcb962cfe796a75d2a0fd00e64c1ba5d1dd6aa95b42df82b0f288d133568d875`.
The downloader and archive/ELF checks were locally verified; the extracted
`azmcp` is 157,361,216 bytes. It was not executed on Windows or deployed to Azure.
For environments using a configured trusted NuGet feed instead, the backend owner
also inspected `Azure.Mcp.linux-x64.3.0.0-beta.48.nupkg`: its native binary is
`tools\any\linux-x64\azmcp` (157,361,216 bytes). Extract that pinned package and pass
the executable's absolute local path via `-McpExecutablePath`; sibling files from
that directory are copied.

If GitHub release restoration fails, the script automatically restores this same
NuGet runtime package through `-McpPackageFeedIndex` (default trusted service index
`https://packagefeedproxy.microsoft.io/nuget/v3/index.json`), discovering its
`PackageBaseAddress` rather than guessing a feed URL. It verifies package SHA512
when that endpoint is supported, then always checks the extracted binary's fixed
SHA256:
`5a290d770c3fd962c69579a827a4690b515543534313ac113c9ebb2469327ce8`.
The manual override also requires this exact verified binary hash and ELF header.
Trusted-feed restoration/extraction and matching native hash were locally verified;
this is integrity checking, not a publisher-signature claim. No global feed settings
or TLS verification are changed. There is no unknown manual artifact prerequisite.
The corporate proxy's `.nupkg.sha512` sidecar returned 404 during verification;
the script treats it as optional, not an extraction prerequisite. The extracted
nuspec must identify exactly `Azure.Mcp.linux-x64` / `3.0.0-beta.48`. Trusted HTTPS
transport, package metadata and the release-derived binary hash do not constitute
NuGet publisher-signature verification.
The backend owner verified Azure.Mcp `3.0.0-beta.48` (preview), the official MCP
C# SDK `2.2.0`, and Agent Framework `1.23.0`. Supply that version's Linux x64
distribution. `AzureMcp__CachePath=/home/.mcp-cache` supplies writable bundle
extraction storage outside the deployed application package. The backend owns the
verified read-only stdio arguments and dedicated-UAMI credential environment.
The verified tool allowlist is `monitor_metrics_query`,
`monitor_workspace_log_query`, `monitor_activitylog_list`, and
`resourcehealth_availability-status_get` (including that exact hyphen).
Subscription Reader supplies read-only activity/resource-health access; the
workspace grant supplies this demo's log queries. Resource health is a platform
availability state, not measured HTTP uptime. Activity and resource-health
payloads are bounded/sanitized by backend policy, not arbitrary caller queries.

`src\frontend` exports Expo assets; only `dist`, `server.mjs`, and `package.json`
are shipped. The production server uses Node built-ins, not Expo's dev server.
The .NET projects are published into `.azure\packages`. These generated files,
parameter files and `.azure\ownership.json` must remain uncommitted.

## Explicit choices and example

The catalog initially listed `gpt-4o-mini`, but actual Azure template validation
rejected version `2024-07-18` as retired. The approved replacement is
`gpt-5-nano`, version `2025-08-07`, `DataZoneStandard`, with observed Sweden
Central DataZone quota usage 0 / limit 2000. The resource is in Sweden Central;
EU DataZone inference can occur elsewhere in the EU. Backend configuration
selects low reasoning effort for this model and uses the OpenAI v1 API.
Catalog/quota observations are **not a capacity reservation**. The script rechecks model
SKU eligibility, available quota and `az webapp list-runtimes --os linux` for
`DOTNETCORE:10.0` / `NODE:24-lts` (both returned by read-only checks).
Choose `Standard` for regional processing,
or `DataZoneStandard` after approving its EU data zone behavior. No Global SKU
is accepted. Free Search's one-service limit and F1/Flex capacity may still block
provisioning; errors are surfaced rather than silently increasing costs.

For existing registrations, the API must be single tenant, issue v2 tokens, and
expose `api://<backend-client-id>/Helpdesk.Access`. The SPA must be single tenant
and have a SPA redirect URI matching the output frontend origin. Configure API
delegated permission and required consent in Entra. Existing registrations are
validated, **never modified or deleted**.

Alternatively `-CreateAppRegistrations` (alias `-CreateEntraApplications`)
explicitly opts into Graph application
creation, API scope/preauthorization, and SPA redirect/delegated-permission setup.
No SPA secret is created. Object IDs are journaled as soon as creation succeeds.
Owned registrations also get their tenant service principals so resource tokens
can be issued. The script does not grant tenant-wide admin consent or bypass
consent policy. If required, a tenant administrator grants consent under the
SPA registration's **API permissions** for the dedicated API's delegated
`Helpdesk.Access` scope. Optional employee restriction is a separate tenant-admin
action: configure **Enterprise applications → dedicated SPA → Properties → User
assignment required**, then assign approved demo employees/groups. It is not a
provisioning default and is not inferred from the audience value.

The script publishes the API scope before assigning SPA preauthorization,
allowing Graph propagation between writes. JSON request bodies are passed by
temporary file for reliable Windows CLI quoting. Temporary resource-scoped
operator grants are all assigned before the model secret is initialized once.
Search uses `disableLocalAuth: true` without `authOptions`, as required by the
service provider for Entra-only access.

The platform's failure-anomaly smart detector is explicitly declared,
ownership-tagged, disabled, and disconnected from action groups. This avoids
an automatically created untagged alert blocking safe reruns or notifying an
unrelated action group. The alert is a global monitoring control; the app,
data, model, and workspace resources remain in the selected region.

Inspect the planned operation without contacting Azure:

```powershell
.\scripts\deploy.ps1 -WhatIf
.\scripts\delete.ps1 -WhatIf
.\infra\tests\safe-scripts.ps1
az bicep build --file infra\main.bicep --stdout
```

An **example for a human-approved future deployment**, not a command executed
during implementation:

```powershell
.\scripts\deploy.ps1 -BackendClientId $apiId -FrontendClientId $spaId `
  -ModelVersion '2025-08-07' -ModelSku DataZoneStandard `
  -DownloadPinnedMcp `
  -HealthAccessToken $delegatedToken -Confirm
```

For the initial owned deployment, after fixture generation:

```powershell
.\scripts\deploy.ps1 -CreateAppRegistrations -DownloadPinnedMcp -SeedDemoData `
  -ModelVersion '2025-08-07' -ModelSku DataZoneStandard `
  -HealthTokenProvider $approvedEmployeeTokenProvider -Confirm
```

For newly created registrations, a token cannot exist before provisioning:
`-HealthTokenProvider` accepts a script block invoked **after** publishing, with
arguments API client ID, SPA client ID, tenant ID, frontend URL. Obtain a delegated
`Helpdesk.Access` token through the approved employee sign-in flow there. Never
commit or print tokens. If neither token option is supplied, the script verifies
public liveness and anonymous rejection, then fails explicitly with an
"authenticated verification incomplete" message instead of claiming success.
Authenticated verification uses `GET /api/examples`, avoiding a billable GPT
request. Function readiness uses its protected admin host-status endpoint with
a master key held only in memory, not an anonymous health endpoint.

## Data, permissions, reruns and cleanup

The source is not seeded by default. Run the separately provided fixture generator
first and pass `-SeedDemoData` only for an empty initial source/state. It uploads
immutable fixture files without overwrite and uploads `manifest.json` **last**.
Committed source or any checkpoint refuses seeding; subsequent changes use the
delta publisher. A partial seed is refused unless `-SeedDemoData
-ResumePartialSeed` is explicitly approved. Recovery rejects unexpected blobs,
checks every existing blob's SHA-256 against the local fixture, uploads only
missing files without overwrite, and commits the manifest last. Differing
bytes are never replaced. CLI upload progress is disabled for JSON parsing.
The Search index schema is PUT via the operator's Entra data-plane token; existing
documents are not deleted. Schema-incompatible changes fail rather than rebuilding
the index.
After Function host readiness, the script polls Search's data-plane index stats
for up to 1,200 seconds (the 15-minute scheduled-ingestion interval plus the
Function's five-minute execution budget). The baseline
on a **fresh `-SeedDemoData` deployment only** must report **176 documents** and
checkpoint **sequence 5**. Every deployment requires storage size **at most
26,214,400 bytes (25 MiB)**. The script reads the committed
`servicenow/manifest.json` and `indexer-state/checkpoint.json` using operator Blob
data identity, validates contiguous manifest descriptors, and requires the
checkpoint to equal the **latest committed manifest sequence**, with its lease
`unlocked/available`. Published Function metadata must contain
`IndexServiceNowKnowledge` and `ProbeHelpdeskAvailability`.
Wrong count/checkpoint, unreleased lease, storage-budget failure or deadline expiry explicitly fails
deployment verification, never resets a checkpoint and never claims success.
`-IngestionTimeoutSeconds` controls the deadline. Reruns **do not assume 176
documents or checkpoint 5**: inserts/deletes/unpublishing can legitimately change
the count. They report the measured document count and verify the latest manifest,
checkpoint/lease and storage budget without rewriting source or state.
`-ExpectedDocumentCount` and `-ExpectedCheckpointSequence` are optional explicit
assertions (default `0` means no fixed count/latest manifest sequence). Source baseline is 227 records;
176 is the employee-visible projected index count, not the source-file count.

Scoped temporary operator grants (Blob Data Contributor, Search Service
Contributor, Key Vault Secrets Officer) are journaled and removed in `finally`.
Pre-existing exact grants are not removed. RBAC and publishing are retried, never
fixed by enabling shared keys. The OpenAI key is read into memory and written to
Key Vault over authenticated REST; it is not printed or saved to disk. Backend
Key Vault references are refreshed after secret storage.

The ownership manifest contains IDs, resource URLs and ownership metadata only.
Retain it across reruns and after partial failures. A group without this manifest
is not adopted. Group tags and every top-level resource must match ownership
before reuse/deletion; unrelated resources prevent the operation.

Deletion removes both journaled subscription Reader assignments **before** the
group, cleans temporary grants, and deletes only the verified owned group.
Partial-deployment cleanup can discover Reader assignments on the tagged MCP
identity and the owned health model's system identity, including when an older
manifest has no health-model fields. It never purges Key Vault. Entra deletion requires the separate
`-DeleteOwnedEntraApplications` opt-in **and**
`-EntraDeletionConfirmation <ownershipId>`, plus a separate ShouldProcess
confirmation for each journaled registration. Existing registrations never enter
that journal. Local ownership records remain for audit and retry.
