# Insurance IT Helpdesk Agent - Deployment Plan

Status: Validated - implementation and Azure deployment approved.

Date: 2026-09-30

## 1. Goal and scope

Create an internal IT helpdesk assistant for employees of a fictional large
insurance company. Answers combine ServiceNow-shaped knowledge retrieved from
Azure AI Search with live, read-only Azure investigation through Azure MCP.
The GPT deployment is hosted in Azure. All infrastructure uses Bicep; deployment
and deletion are orchestrated by PowerShell.

The workspace is a new, empty Git repository. There is no application code or
existing infrastructure to preserve.

Confirmed user choices:

- Absolute-minimum-cost demo, using App Service Free F1 where supported.
- Microsoft Entra ID employee sign-in is required.
- Implementation approved on 2026-09-30.
- Deployment approved on 2026-09-30 at 12:17 CEST; subscription, Sweden
  Central/EU processing, and planned permission assignments reconfirmed.
- Subscription: Tyrone-Subscription-CreditCard
  (d860292c-5d2c-4df3-b7c8-332bd46882d1).
- Tenant: 47c94d43-bd0b-4cc0-9c81-412496225c31.
- Preferred location: Sweden Central wherever supported.
- Dedicated frontend/API Entra registrations will be created only during an
  explicitly approved deployment, with recorded ownership.
- The pinned Azure MCP 3.0.0-beta.48 preview was explicitly approved for this
  demo after its hosted-identity/tool-schema compatibility was verified.
- Azure preflight rejected the initially considered gpt-4o-mini version as
  retired. GPT-5 nano version 2025-08-07 in Sweden Central with EU
  DataZoneStandard processing was explicitly approved as the replacement.

Initial read-only Azure checks found Linux F1 listed in Sweden Central and no
existing Search service in this subscription. Two unrelated OpenAI accounts
exist in Germany West Central; neither will be reused without approval.
The Microsoft.Web quota API reports a regional limit of 30 with
isQuotaApplicable=false; this is not proof of current available F1 capacity.

This is a small proof of concept representing a large insurer, not a production
insurance deployment. No real customer, employee, policy, or claim data is used.
Native mobile-store delivery, real ServiceNow credentials, ticket creation,
automated remediation, high availability, private networking, and production
compliance certification are outside this first implementation.

## 2. Architecture

```text
Employee browser
  |
  | Entra ID sign-in (authorization code + PKCE)
  v
Frontend App Service
  Expo / React Native Web / TypeScript
  Chat | Example questions | Knowledge source details
  |
  | HTTPS API call + access token (never a model API key)
  v
Backend App Service
  ASP.NET Core / .NET 10 LTS / Microsoft Agent Framework
  |
  +-- Azure OpenAI GPT deployment
  |     Endpoint + API key, resolved server-side from Key Vault
  |
  +-- SearchKnowledge tool --> Azure AI Search
  |     Evidence snippets, article IDs, application aliases, citations
  |
  +-- Official MCP C# client --> Azure MCP Server child process (stdio)
        Read-only mode + exact tool allowlist + fixed subscription scope
        Dedicated managed identity --> ARM / Azure Monitor / Log Analytics

Private Blob Storage
  ServiceNow-shaped baseline + immutable delta batches + manifest
  |
  v
Timer-triggered .NET isolated Azure Function (every 15 minutes)
  Lease + checkpoint + incremental upserts/deletes --> Azure AI Search

Application Insights + Log Analytics
  Backend request/dependency telemetry and ingestion telemetry
  Optional low-frequency Function availability probes --> live MCP evidence
```

### Hosting interpretation

"React Native on App Service" means React Native Web, exported by Expo and
served as a browser application. App Service does not host an iOS/Android
binary. React Native components will be used, with responsive desktop/mobile
layouts and accessible navigation.

Two App Service apps will use one Linux F1 plan where the selected region and
runtime permit it. The frontend serves the built web assets using a small
production web server with route fallback; it does not run the Expo dev server.
The backend deploys published .NET output plus a pinned, packaged Azure MCP
executable. No container registry, extra MCP web app, or public MCP endpoint
is needed.

The backend owns the MCP connection and child-process lifecycle. It initializes
on demand, reinitializes after worker recycling, times out bounded tool calls,
and reports MCP unavailability explicitly. Packages are restored at build time,
not downloaded with an unpinned npx/dnx command on each request.

F1 has daily CPU limits, no Always On, idle shutdown, and no SLA. These are
accepted demo limitations, not issues to conceal with keep-alive traffic.
If a verified hosting constraint makes F1 unsuitable, present the evidence and
obtain approval for a shared Linux B1 plan; never silently select a paid SKU.

## 3. Services and cost controls

| Component | Service / default SKU | Cost decision |
| --- | --- | --- |
| Frontend and backend | Two web apps, one Linux Free F1 plan | No compute charge if supported; cold starts and quotas accepted |
| Scheduled indexer | Functions Flex Consumption FC1, 512 MB, no always-ready instances | Scale to zero; bounded batches; free grants are subscription-dependent |
| Synthetic data and Function storage | One StorageV2 Standard LRS account | Private containers for source, state, deployment, and host needs |
| Knowledge retrieval | Azure AI Search Free | One index; 50 MB service limit; only one Free service per subscription |
| Model | Azure OpenAI S0 account with a pay-per-token GPT mini deployment | No provisioned throughput; deployment name/version/capacity parameterized |
| Secrets | Key Vault Standard | Store model API key; avoid committed or printed secrets |
| Telemetry | One Application Insights component and one Log Analytics workspace | Pay-as-you-go; sampling, short retention where permitted, daily ingestion cap |
| Access | Managed identities and Azure role assignments | No extra hosting charge |

GPT model selection uses gpt-5-nano version 2025-08-07 after actual Azure
preflight rejected the earlier gpt-4o-mini candidate as retired. The user
approved EU DataZoneStandard inference with a Sweden Central resource.
The backend uses the OpenAI v1 API, low reasoning effort, and a bounded
2,000-token completion budget per model round. Global deployments remain
disallowed. Hosted tool calls were verified after switching from minimal to
low reasoning. Token costs remain traffic-dependent; no universal
lowest-total-cost claim is made.

Use ordinary full-text/BM25 Search with application/category filters, synonyms,
and bounded result counts first. This satisfies the requested grounding
without an embedding deployment or paid semantic ranking. Hybrid/vector
retrieval is a separately evaluated upgrade, not a required first-run cost.

Do not provision Cosmos DB, SQL, Redis, APIM, ACR, Premium Functions, paid
availability web tests, or a hosted Foundry agent service.

No exact monthly estimate is asserted until region, model, traffic, and
subscription grants are known. F1 and Search Free are not the entire bill:
GPT tokens, Blob operations/storage, Key Vault operations, Function usage
outside grants, telemetry ingestion, and egress can still incur charges.
Set request/output/tool-call limits and per-user rate limits. Telemetry caps
are not a whole-solution spending cap.

## 4. Backend agent and user experience

Use Microsoft Agent Framework, Azure OpenAI client integration, Azure Search
Documents SDK, and the official ModelContextProtocol C# SDK. Pin compatible
versions after a compilation spike; explicitly document preview packages if
any required integration has not reached general availability.

One helpdesk agent is sufficient initially; avoid unnecessary multi-agent
handoffs and token overhead.

The agent has two evidence paths:

1. SearchKnowledge: retrieve published employee-facing KBs and sanitized
   resolved incidents/problems; filter audience, application, and lifecycle.
2. Azure investigation: resolve known application aliases to configured Azure
   resource IDs, then call allowed discovery, metrics, activity, health, and
   workspace-query tools through Azure MCP.

For ambiguous application names, ask the employee to identify the application
instead of inspecting an arbitrary resource. Subscription and tenant are
server-controlled and cannot be overridden by user prompts.

Responses contain:

- Direct answer and actionable troubleshooting steps.
- Knowledge citations with KB/incident identifiers and supporting snippets.
- Separately labeled live Azure evidence, resource identity, UTC query window,
  observation timestamp, and caveats.
- Explicit "not enough evidence" or dependency errors when appropriate.
- Safe escalation guidance; no invented ticket number or claimed remediation.

Expose chat/session and health APIs, and a curated example-question catalog.
Use a bounded response contract with answer, knowledgeSources, azureEvidence,
and warnings. Streaming may use authenticated fetch if validated on F1.
Keep conversation state bounded and ephemeral for the demo, isolated per
authenticated user; worker restart can reset it. No model-generated citations
are accepted unless they match retrieved evidence.

Frontend pages:

- Chat: question input, conversation, response progress, citations, errors.
- Example questions: categorized cards that prefill or send a question.
- Knowledge details: show authorized synthetic source excerpts; no pretend
  links to a real ServiceNow instance.

Treat retrieved text and tool results as untrusted data, never instructions.
Prevent prompt injection from overriding tool restrictions or authorization.
Do not log tokens, model keys, complete prompts, or raw sensitive tool output.

## 5. Synthetic ServiceNow data

Generate deterministic records for a fictional insurer, with a fixed random
seed and UTC timestamps. Target approximately:

- 60 knowledge articles (kb_knowledge).
- 120 incidents (incident), including resolved reusable cases and open issues.
- 20 problem records (problem).
- 15 change records (change_request).
- 12 application configuration items (cmdb_ci_business_app).

Keep source/index data well below the Free Search limit; validate actual
indexed size, targeting under 25 MB to leave headroom.

Use ServiceNow Table API-style envelopes with a result array and realistic
fields: sys_id, number, sys_created_on, sys_updated_on, sys_mod_count,
short_description, description, text, workflow_state, incident_state, priority,
category, subcategory, assignment_group, business_service, cmdb_ci,
close_notes, and active. Include reference objects with value/link/display_value
where appropriate. Table-specific fields are only used on relevant records.

Raw source records can include realistic synthetic restricted work notes,
but the ingestion projection must exclude them from employee-facing retrieval.
Only authorized published KBs and sanitized resolution material become
retrievable knowledge. Open incidents, changes, and CMDB entries provide
clearly labeled context, not validated troubleshooting instructions.

Representative applications: Claims Workbench, Policy Administration,
Broker Portal, Underwriting Desktop, Actuarial Analytics, Finance Reporting,
Document Vault, Employee IT Helpdesk, and corporate identity/VPN services.

Only the actual deployed demo resources have live Azure IDs. Map "Employee IT
Helpdesk" and frontend/backend aliases to those resources after deployment.
Other fictional applications must never be reported as real Azure workloads
unless an operator explicitly maps them to existing authorized resources.

### Example questions and required evidence

| Question | Expected evidence |
| --- | --- |
| I cannot sign in to Claims Workbench after changing my password. | Identity/session KB and sanitized resolved incident |
| My MFA prompt goes to my old phone. What should I do? | MFA recovery KB; no bypass advice |
| Policy Administration sends me around a login loop. | Browser/SSO KB and known problem |
| The Broker Portal URL returns 404. Is there a new address? | Published URL-change KB/change context |
| My VPN connects but Underwriting Desktop does not load. | VPN/DNS/split-tunnel KB and incident |
| I get access denied when opening a claim attachment. | Document Vault access/permissions KB |
| Actuarial Analytics is slow during month-end processing. | Known problem, maintenance context, safe troubleshooting |
| Why has my Finance Reporting export been blocked? | Synthetic DLP/export KB |
| Is this IT helpdesk application available right now? | Live Azure health/metrics and latest probe if available |
| What was the helpdesk backend's availability over the last 24 hours? | Measured availability events with observation window/coverage |
| The helpdesk URL stopped responding. Is this a known issue or an Azure outage? | Both indexed knowledge and live Azure evidence |
| Did a deployment or configuration change occur before the errors started? | Bounded Azure activity log and request error telemetry |
| Restart the helpdesk backend and disable authentication. | Refuse write operations; explain read-only scope |
| Is the fictional Broker Portal down in Azure? | Explain lack of live mapping instead of inventing telemetry |

## 6. Delta ingestion design

Use a timer-triggered Function rather than an Azure AI Search scheduled indexer.
The Function controls normalization, visibility filtering, chunking, delta
ordering, and deletion propagation.

Blob layout:

- baseline/<table>.json: initial API-shaped records.
- changes/<sequence>.json: immutable insert/update/delete batches.
- manifest.json: committed batches, sequence ranges, hashes, record counts.
- state/checkpoint.json: committed sequence and initial-load status.
- state/documents/: per-record indexed version/chunk metadata as needed.

The change envelope carries operation, table, sys_id, and monotonic sequence.
Its record payload resembles a ServiceNow API response; tombstones and sequence
metadata are simulator additions, not claimed native Table API fields.

Protocol:

1. Producer uploads complete immutable batches, then atomically publishes
   manifest changes using ETags. No unpublished batch is consumed.
2. Timer acquires a renewable Blob lease, reads the committed checkpoint, and
   processes only newer committed sequences in order.
3. Parse and validate records, normalize HTML safely, apply audience/status
   projection, and chunk eligible content with deterministic document IDs.
4. Upsert changed documents; explicitly delete tombstoned, unpublished, or
   no-longer-visible records and obsolete chunks after content shrinks.
5. Check every Search batch item's result. Retry transient failures with
   bounded backoff; leave checkpoint unchanged on any unfinished failure.
6. Advance the checkpoint with lease/ETag protection only after all changes
   through that sequence have been applied successfully.

This is at-least-once processing with idempotent writes, not an unsupported
exactly-once claim. Replays cannot create duplicates. Do not use timestamps
alone for correctness: equal timestamps, delayed uploads, and clock skew
must not lose updates. A no-change tick performs no Search document writes.
Bound work per invocation and resume from the last committed batch.

Malformed data blocks progress with a clear error and telemetry; it is not
silently skipped. Recovery and explicit reindex are operator-only actions.
A seed/simulation script will generate and upload inserts, updates, deletes,
unpublication, and same-timestamp changes for demonstrations.

## 7. Identity and permissions

### Validation Proof

Architecture diagram incremental validation on 2026-09-30, after 17:08 CEST:
`npm run typecheck` and `npm test` passed (13 tests and production Expo export).
Azure CLI/authentication and `az bicep build` passed. Named subscription
`validate` and `what-if` with approved deployment parameters and request
`snowdemo-diagram-validation-3a67f88b` passed (Succeeded; 20 Modify, 9 NoChange,
no Create/Delete). Policy/ownership/region checks passed. Frontend static
identity requirements remain unchanged. Local browser verified the accessible,
theme-aware diagram at 1440px, 760px and 320px with no page overflow.
See the frontend-only diagram redeployment checklist below for release scope.

Pre-deployment verification passed on 2026-09-30 before deployment approval; no application resources or Entra
registrations were created:

| Check | Command or evidence | Result |
| --- | --- | --- |
| Solution build | `dotnet build Insurance.Helpdesk.slnx --nologo -v minimal` | Pass, zero warnings/errors |
| .NET tests | `dotnet test Insurance.Helpdesk.slnx --no-build --nologo -v minimal` | 51 passed: 32 backend, 19 indexing |
| Dependency reproducibility | `dotnet restore Insurance.Helpdesk.slnx --locked-mode` | Pass; backend SDK dependency lock persisted |
| Model request | Offline SDK HTTP transport test | OpenAI v1 route, low reasoning in the deployed setting, 2,000 completion tokens, no temperature |
| Frontend | Integration typecheck, tests, Expo production export | Pass; NN-inspired restyle additionally verified in local browser, light/dark and 320/760/980/1440px widths |
| Script safety | `infra\tests\safe-scripts.ps1`, `tests\scripts\deployment-contract.ps1` | Pass; WhatIf made zero cloud calls |
| Bicep/provider preflight | Named helper: CLI/auth/build, subscription `validate`, `what-if` | Pass in Sweden Central, 28 Create changes, no Modify/Delete |
| Policy | Azure CLI assignment and initiative inspection | Only SecurityCenterBuiltIn observed; default effects: 64 Audit, 98 AuditIfNotExists, 123 Disabled |
| RBAC | Static role review in section 14 | Verified; effective hosted access remains a deployment test |

The standard validation helper collided with unrelated deployment metadata
named MAIN in North Europe. A session-local helper retained its validation
sequence but used `nn-snow-agent-preflight-swedencentral` as the explicit name.
Its parameters contain clearly labeled validation-only SPA/API identifiers,
not real registrations and not deployable authentication configuration.
The initial retired-model failure was resolved by the user-approved GPT-5
nano 2025-08-07 DataZoneStandard replacement and provider checks were rerun.
The typed reasoning property is experimental in pinned OpenAI SDK 2.14.0;
the production client uses the Azure OpenAI v1 endpoint.

Policy inspection used Azure CLI because no policy MCP tool was available.
No configured default Deny/DeployIfNotExists effect was observed. Audit
findings are not a production-compliance certification.
The initial post-deployment acceptance gates were completed: model capacity,
employee sign-in, hosted Linux MCP identity, real model/tool answers, Search
storage size, indexing and scheduled no-change telemetry were verified.
Optional HTTP probes remain disabled, so measured application uptime is
unknown rather than an accepted success result.

### Employee authentication

Use single-tenant Entra ID app registrations for the SPA and backend API, an
API access scope, PKCE, exact redirect URIs, and backend issuer/audience/scope
validation. Require approved employee/group assignment where tenant policy
permits. Cross-origin API access is restricted to the frontend origin.
The SPA has no client secret.

Entra app registrations are directory objects, not ordinary ARM resources.
Accept existing registration IDs or create them in an explicit PowerShell
setup step using Microsoft Graph/Azure CLI with operator consent. Record
ownership so deletion never removes pre-existing registrations.

### Runtime role matrix

| Identity | Role and scope | Purpose |
| --- | --- | --- |
| Backend identity | Search Index Data Reader on the Search service | Query knowledge only |
| Backend identity | Key Vault Secrets User, restricted to its model secret where feasible | Resolve Azure OpenAI key server-side |
| Azure MCP user-assigned identity | Reader at the confirmed subscription | Resource discovery, configuration, health, and metrics reads |
| Azure MCP user-assigned identity | Log Analytics Reader on the demo workspace only, if needed | Read workspace telemetry; no estate-wide log access by default |
| Indexer identity | Storage Blob Data Reader on source container | Consume source and committed manifest |
| Indexer identity | Storage Blob Data Contributor on state container | Lease/checkpoint persistence |
| Indexer identity | Search Index Data Contributor on Search service | Upsert/delete index documents |
| Function host identity | Required identity-based host/deployment storage roles only | Functions host, timer coordination, package access |
| Deployment operator | Provisioning, directory setup where requested, and role-assignment permissions | Deploy infrastructure; never used by the running agent |

Confirm precise host storage role requirements for the selected Functions
extension/deployment configuration; do not assume source-container access alone
is enough for identity-based AzureWebJobsStorage.

Use a dedicated MCP identity so index-writing and secret-reading permissions
are not part of MCP's intended Azure authority. Attach it to the backend and
explicitly select it for MCP authentication. Verify the pinned MCP version's
headless App Service managed identity configuration; do not fall back to a
developer login, CLI cached credential, or deployment operator identity.

A child process is not a hard OS security boundary from its parent. Therefore
also enforce an exact tool/command allowlist, fixed scopes, and parameter
validation in the backend. Never pass model keys or unnecessary secrets into
the MCP process environment.

MCP runs with read-only mode enabled. Do not expose generic CLI execution,
deployment, restart, configuration mutation, key-listing, Key Vault,
storage-data access, or other secret-returning tools. Validate subcommands,
not merely namespace-level hints. Refuse startup if the expected tool/schema
contract changes.

Subscription Reader is read-only but can expose estate metadata. Employee
answers are restricted to configured applications/resource scopes; raw estate
inventories and logs are not universally disclosed. Additional workspace
access for existing applications requires explicit operator approval.

## 8. Uptime and operational evidence

Azure resource state "Running" is not proof of HTTP availability or an uptime
percentage. Request-success rate is not the same as endpoint uptime.

Instrument actual backend requests/dependencies in Application Insights.
For measured availability, add a configurable low-frequency Function probe
of the deployed frontend and a non-sensitive backend liveness endpoint.
Store timestamped availability results, including latency, timeout, and target.
The endpoint exposes no configuration or secrets; employee APIs remain
authenticated.

Calculate observed availability from successful scheduled probes divided by
valid executed probes. Report missed samples and the measured interval
separately. Initially there may be no 24-hour history; say so. This is sampled
single-location availability, not contractual SLA or end-to-end employee
login success.

A Function probe can wake F1 apps and consume CPU/storage/telemetry. Keep the
frequency configurable and explicit (initially 15 minutes), never advertise
it as an Always On workaround. No fabricated uptime values or seeded "live"
Azure logs. Probe history survives app worker recycling in Azure telemetry.

## 9. Bicep and PowerShell deliverables

Planned project layout:

```text
src/
  frontend/                  Expo React Native Web app
  backend/                   ASP.NET Core agent API
  indexer/                   .NET isolated Functions
tests/
  backend/                   Agent/tool/auth/evidence contract tests
  indexer/                   Delta/projection/checkpoint tests
  frontend/                  UI and example catalog tests
data/
  servicenow/                Deterministically generated API-shaped fixtures
infra/
  main.bicep                 Subscription orchestration and subscription RBAC
  main.bicepparam            Non-secret environment parameters
  modules/                   Hosting, AI, storage, monitoring, identity/RBAC
scripts/
  deploy.ps1
  delete.ps1
  generate-demo-data.ps1
  publish-demo-delta.ps1
README.md
```

Bicep owns all Azure resources, settings, and Azure RBAC assignments. Search
indexes/documents and Blob payloads are data-plane artifacts: deployment
scripts/bootstrap code create them through the SDK/REST APIs after Bicep
provisions the service. They are not incorrectly represented as native ARM
Search-index resources.

### deploy.ps1

1. Accept explicit subscription, region, resource group, prefix, tenant,
   registration IDs/setup choice, and model deployment parameters.
2. Check tools/runtimes, permissions, regional SKU/runtime availability,
   Free Search slot, model quota, and existing-resource ownership.
3. Show selected SKUs and cost caveats. Do not silently upgrade or replace
   unrelated existing resources.
4. Build/test applications; compile/lint Bicep; show what-if.
5. Deploy subscription/resource-group Bicep and role assignments.
6. Set the model key into Key Vault without printing/exporting it; enable the
   backend Key Vault reference. Retry bounded RBAC propagation delays.
7. Bootstrap Search schema, upload baseline/deltas, publish frontend/backend
   and Function packages, and configure approved Entra redirects/scopes.
8. Verify health, initial scheduled ingestion, citations, MCP identity/tools,
   authentication, and live telemetry; output URLs and resource ownership
   manifest with no secrets.

Infrastructure updates and data seeding are idempotent. Rerunning deploy does
not reset the ingestion cursor or erase edited fixtures. A reindex/reset is
a separate explicit operator action.

### delete.ps1

- Support PowerShell ShouldProcess, WhatIf, and explicit confirmation.
- Display subscription, resource group, and owned resources before deletion.
- Refuse ambiguous ownership or deletion of unrelated/pre-existing resources.
- Remove recorded subscription-scoped Reader assignment before deleting the
  owned resource group; RG deletion alone does not clean subscription RBAC.
- Remove only explicitly owned Entra registrations if separately confirmed.
- Verify completion and surface failures; leave existing model/search services
  untouched when the deployment was configured to reuse them.
- Explain Key Vault soft-delete/name-reuse behavior; never purge by default.
- Never delete local source folders.

## 10. Resource inventory and deployment readiness

| ARM resource type | New count | Capacity check required before provisioning |
| --- | --- | --- |
| Microsoft.Resources/resourceGroups | 1 | Confirm dedicated ownership and subscription |
| Microsoft.Web/serverfarms | 2 | Linux F1 for web apps; FC1 for Functions |
| Microsoft.Web/sites | 3 | Two web apps and one Function app; runtime/region support |
| Microsoft.Storage/storageAccounts | 1 | Regional account quota and unique name |
| Microsoft.Search/searchServices | 1 | Free slot available; otherwise stop for user decision |
| Microsoft.CognitiveServices/accounts | 1 or reuse | Regional GPT availability and model token quota |
| Microsoft.CognitiveServices/accounts/deployments | 1 or reuse | Lowest-cost compatible GPT deployment |
| Microsoft.KeyVault/vaults | 1 | Unique name and soft-delete conflicts |
| Microsoft.Insights/components | 1 | Telemetry settings and pricing |
| Microsoft.OperationalInsights/workspaces | 1 | Retention and allowed ingestion |
| Microsoft.ManagedIdentity/userAssignedIdentities | 1 | Dedicated Azure MCP identity |
| Microsoft.Authorization/roleAssignments | Role matrix above | Scope, effective permissions, and operator authorization |

Read-only checks have confirmed the subscription and Sweden Central preference,
Linux F1 region listing and .NET isolated Flex runtime version 10.
Actual provider validation rejected the earlier gpt-4o-mini catalog entry as
retired. The approved gpt-5-nano version 2025-08-07 DataZoneStandard replacement
has observed regional quota usage 0/2000; quota is not a capacity reservation.
Current regional prices, actual deployment capacity, directory permissions,
effective runtime permissions, and employee assignment remain deployment
verification gates. No resources or app registrations have been provisioned.

## 11. Implementation sequence and acceptance criteria

1. Compatibility spike
   - Pin .NET/Agent Framework/MCP SDK/server versions.
   - Compile tool-calling agent; validate F1 packaging and managed identity.
   - Confirm regional/model/tier choices before provisioning.
2. Fixtures and delta indexer
   - Create schema, deterministic synthetic data, and change simulation.
   - Test initial load, no-op tick, insert/update/delete, unpublication,
     stale chunks, equal timestamps, lease exclusion, crash/replay, and
     partial Search failure without premature checkpoint advancement.
3. Backend
   - Add authenticated agent endpoints, Search grounding, allowed MCP tools,
     structured citations, timeouts, scoped sessions, and rate limits.
   - Verify search identity cannot write and MCP cannot mutate resources,
     query unauthorized workspaces, switch subscriptions, or obtain secrets.
4. Frontend
   - Build chat, example questions, source details, responsive/accessibility
     behavior, sign-in, and explicit loading/error/empty states.
5. Infrastructure and scripts
   - Implement Bicep and safe deploy/delete scripts.
   - Validate rerunnable deployment and ownership-aware cleanup.
6. End-to-end validation
   - KB-only questions produce supporting source IDs and correct steps.
   - Azure-only questions execute real MCP reads and label observation times.
   - Mixed questions combine both paths without confusing synthetic incidents
     with actual outages.
   - Unauthorized users receive 401/403, not agent output.
   - No-evidence/model/Search/MCP failures produce honest, visible errors.
   - Delta simulation changes only affected documents; unchanged records are
     not reindexed; all Free-tier storage thresholds are measured.
   - Browser smoke test, .NET tests, frontend type/build/tests, Bicep checks,
     script WhatIf, and deployed identity/telemetry checks pass.

Use azure-validate before deployment, then azure-deploy for the deployment
execution and verification workflow. Do not deploy during planning.

## 12. Planning progress and approval

- [x] Inspect empty workspace and existing changes.
- [x] Confirm demo cost preference and employee authentication.
- [x] Research official framework/MCP/hosting/Search documentation.
- [x] Define architecture, data, delta behavior, identities, and scenarios.
- [x] Define infrastructure, scripts, tests, cost controls, and risks.
- [x] User approves implementation of this architecture.
- [x] Confirm subscription, tenant, Sweden Central preference, and Entra setup choice.
- [x] Implement core projection, deterministic 227-record fixtures, and delta producer/indexer.
- [x] Build/publish indexer with two generated timer entry points and zero build warnings.
- [x] Pass 19 indexing tests and all six local delta simulation scenarios.
- [x] Implement and verify frontend: typecheck, web export, eight tests, browser/mobile smoke.
- [x] Implement backend with four guarded MCP tools, Search citations, and Entra authorization.
- [x] Pass integrated .NET build with zero warnings and 47 tests (28 backend, 19 indexing).
- [x] Pass integrated frontend typecheck, eight tests, and production web export.
- [x] Verify automatic Linux MCP packaging with pinned archive/binary hashes and ELF architecture.
- [x] Compile final Bicep and pass mutation-free deployment/deletion safety checks.
- [x] Verify initial seed/rerun counts, checkpoint/lease readiness, storage budget, and timer deadline.
- [x] Ignore deployment ownership state in Git and document future deploy/delete commands.
- [ ] Perform Azure validation handoff; retain live-deployment checks as unverified gates.
- [ ] Verify final pricing/capacity and deployed identities after deployment approval.
- [ ] Implement, validate locally, obtain deployment approval, and provision.

Application implementation and local validation are complete. No Azure resources
or directory app registrations have been created, and deployment remains
unapproved. Live GPT calls, Linux MCP execution on App Service, effective Azure
roles, real employee sign-in, and indexed storage usage require an approved
deployment and end-to-end smoke tests.

## 13. References

- [Agent Framework MCP tool integration](https://learn.microsoft.com/en-us/agent-framework/agents/tools/local-mcp-tools)
- [Azure MCP installation and language integrations](https://learn.microsoft.com/en-us/azure/developer/azure-mcp-server/get-started)
- [Azure MCP read-only and tool-selection parameters](https://learn.microsoft.com/en-us/azure/developer/azure-mcp-server/tools/)
- [Azure MCP Monitor tools](https://learn.microsoft.com/en-us/azure/developer/azure-mcp-server/tools/azure-monitor)
- [Expo and React Native Web export](https://docs.expo.dev/workflow/web/)
- [App Service hosting plans and shared-compute constraints](https://learn.microsoft.com/en-us/azure/app-service/overview-hosting-plans)
- [Functions Flex Consumption](https://learn.microsoft.com/en-us/azure/azure-functions/flex-consumption-plan)
- [Azure AI Search service limits](https://learn.microsoft.com/en-us/azure/search/search-limits-quotas-capacity)
- [Azure AI Search RBAC](https://learn.microsoft.com/en-us/azure/search/search-security-rbac)
- [Azure OpenAI pricing](https://azure.microsoft.com/en-us/pricing/details/azure-openai/)

## 14. Validation workflow

Recipe: standalone Bicep at subscription scope, executed with Azure CLI and
PowerShell. Directory registration creation remains a future deployment step.

- [x] All validation checks pass
  - [x] Recipe core validation: CLI, authentication, Bicep build, subscription
    provider validation, and what-if using a uniquely named validation request.
  - [x] Optional linting: compiler/linter emitted no warnings.
  - [x] Recipe Azure Policy validation: inherited/default initiative reviewed.
  - [x] Core local checks: Azure CLI authentication observed, .NET build/tests,
    frontend typecheck/tests/export, Bicep compile.
  - [x] Core server-side checks: subscription template validate and what-if
    using explicitly labeled validation-only identity parameters.
  - [x] Azure Policy validation for the selected subscription.
  - [x] Static role assignment verification.
  - [x] Script parsing, non-mutating WhatIf, initial-seed/rerun readiness,
    checkpoint/lease/storage/deadline checks.
  - [x] Record proof and complete the official validation workflow.

### Incremental frontend-only redeployment validation (2026-09-30)

- [x] Frontend typecheck and tests: `npm run typecheck`; `npm test` (12
  passing frontend tests; test lifecycle also completed the Expo web export).
- [x] Route, content, identifiers and responsive checks: `/architecture` has
  its own direct SPA route, explains the request/indexing paths, live settings,
  tool allowlist and identities, and includes no tenant/client/resource IDs.
  Browser inspection covered 1440px, 760px and 320px in light/dark themes;
  320px has no horizontal overflow after the single-column breakpoint.
- [x] Recipe core revalidation: authenticated subscription CLI, Bicep build,
  server-side subscription validation and what-if completed with the existing
  validation-only parameters and unique deployment name. What-if reported one
  Create and 28 Modify against those intentionally placeholder identities;
  it provisioned no resources. This infrastructure preview is not part of
  the frontend-only redeploy.
- [x] Static role review: frontend-only source changes introduce no identity,
  role, backend, Function, model, Search or infrastructure changes.
- [x] Built a unique frontend-only ZIP (224,600 bytes); its exported web
  bundle includes the documented agent configuration. The Azure ownership
  manifest matched the subscription, resource group, region and frontend tag;
  no temporary role grants remained.
- [x] The operator reconfirmed deployment to the existing
  `snowdemo-rg`/Sweden Central target. Published only the frontend with
  `az webapp deploy --type zip --clean true`. No Bicep, app settings, Entra,
  backend, Function, index, checkpoint or source data was changed.
- [x] Hosted verification: frontend state Running; `/health/live`,
  `/architecture`, JavaScript bundle and runtime configuration all returned
  HTTP 200. Live browser verification confirmed architecture/model/indexing
  sections, navigation state and no horizontal overflow at 1440px and 320px.

### Architecture diagram frontend-only redeployment (2026-09-30)

- [x] Recipe core validation: Azure CLI/authentication and Bicep compile.
  The helper initially lacked the parameter file, then hit an unrelated
  subscription deployment named MAIN in North Europe. Repeated the server-side
  validate/what-if with approved `.azure/deployment.parameters.json` and unique
  name `snowdemo-diagram-validation-3a67f88b`: validation Succeeded, preview
  20 Modify / 9 NoChange / no Create or Delete. No infrastructure deployed.
- [x] Optional linting: Bicep compilation and frontend TypeScript checks pass.
- [x] Azure Policy: current inherited assignment remains the built-in Microsoft
  cloud security benchmark (216 definitions). This frontend-only release
  changes no Azure resource configuration, SKU, region, tags or permissions.
- [x] Application validation: `npm run typecheck`, `npm test` and its Expo
  web export passed, with 13 tests including diagram source and download route.
  Browser verified 11 nodes, 10 arrows, accessible description, light/dark
  themes and no page overflow at 1440px, 760px and 320px.
- [x] Static roles: frontend requires no managed identity or Azure data roles;
  diagram changes introduce no backend, indexer, infrastructure or RBAC changes.
- [x] Operator reconfirmed the existing subscription, snowdemo-rg and Sweden
  Central target. Live resource/group ownership tags match the ownership
  journal; frontend is Running and has no managed identity.
- [x] Official validation workflow completed; published the 228,274-byte
  frontend-only ZIP with `az webapp deploy --type zip --clean true`.
  Deployment `f1823953-41c5-47b7-9b93-f3fcc9966a0c` is RuntimeSuccessful,
  one successful instance and zero failures. No backend, Function, Azure
  configuration, roles, Search documents or checkpoints were changed.
- [x] Hosted health, architecture route, runtime config, editable diagram and
  validated bundle `index-121c7720b893b225ffad22cd2b15bbfc.js` returned HTTP 200.
  Hosted diagram source exactly matches the local file. Browser confirmed
  11 nodes / 10 arrows, keyboard-focusable scrolling, light/dark rendering,
  no page overflow on direct loads at 320px and 1440px, and working navigation
  to Example questions. Frontend is Running with no managed identity; live
  MCP roles remain subscription Reader and scoped Log Analytics Reader.

### Static role assignment verification

Verified principals, roles, and scopes in infra/main.bicep and
infra/resources.bicep against the implemented service operations:

- Backend system identity: Search Index Data Reader on Search; Key Vault
  Secrets User on its dedicated vault; Monitoring Metrics Publisher on
  Application Insights.
- Dedicated MCP identity: Reader on the confirmed subscription; Log Analytics
  Reader on the dedicated workspace. No writer or secret data role.
- Indexer system identity: Search Index Data Contributor on Search; standard
  identity-based Function host Blob/Queue/Storage Account roles on the one
  dedicated storage account; Monitoring Metrics Publisher on Insights.
- Frontend: no Azure data identity needed; employee sign-in uses the SPA/API
  registrations.

The Function host roles are account-scoped because source, host, deployment,
and checkpoint containers share the cheapest one-account demo layout. This
does not provide hard source/state permission separation inside that account.
The agent's MCP identity has none of those host/index-write roles.

During static verification, direct worker telemetry required explicit
SetAzureTokenCredential configuration to match disabled local authentication.
That wiring was added; the backend's App Service automatic instrumentation uses
its AAD authentication setting. Live ingestion and effective roles still need
deployment verification.

## 15. Approved deployment execution

User approved deployment on 2026-09-30 at 12:17 CEST, then reconfirmed the
subscription, Sweden Central/EU DataZoneStandard, and planned RBAC grants.
The Bicep/CLI recipe is used directly; AZD environment/tag and Container Apps
checks are not applicable to this App Service/Functions deployment.

- Resource group: `snowdemo-rg`; ownership journal: `.azure/ownership.json`.
- ARM deployment: `snowdemo-2c2b1133`; the 12 app/data/model/workspace
  resources are in Sweden Central. One disabled smart-detector rule uses
  Azure's required global location.
- Infrastructure provisioning: succeeded after removing the incompatible
  Search `authOptions` property; `disableLocalAuth: true` remains enabled.
- Actual-identity provider validation and what-if rerun: passed; changes were
  confined to the journaled deployment.
- Windows CLI JSON bodies now use temporary files and explicit Graph content
  type. API scopes are registered before SPA preauthorization. Secret
  initialization runs once after all temporary operator grants are assigned.
- A platform-created Failure Anomalies rule initially blocked ownership
  checks. Its sole scope was verified as this demo's Insights. The user
  approved disabling/tagging this exact rule and disconnecting its reference
  to an unrelated action group; that action group was not modified.
- First seed upload succeeded but CLI progress output corrupted JSON parsing.
  The user approved partial-seed recovery. Existing bytes were SHA-256
  verified, missing blobs uploaded without overwrite, and manifest committed
  last; source/state/checkpoints were not reset. Uploads now disable progress.
- Frontend and backend packages published. Frontend HTTPS is responsive;
  backend `/health/live` returned 200. Employee Entra sign-in was completed
  manually, and the authenticated examples API returned 200 with 14 examples.
- Initial indexer package validation, extraction and upload succeeded, but
  trigger synchronization returned 500. Host requests failed or timed out.
  Deployment retries were stopped rather than repeated indefinitely.
- Added explicit `FUNCTIONS_EXTENSION_VERSION: ~4` to Bicep and live settings;
  immediate recheck still failed. Managed-identity diagnostics reported no
  detected configuration failures. No error traces were returned by the
  queried workspace. These observations do not establish the root cause.
- Initial storage inspection found no host containers or checkpoint, and
  Search statistics showed zero documents. These observations preceded the
  runtime and binding corrections below.
- Hosted knowledge and Azure-health questions returned withheld answers for
  unverified citations, with no knowledge sources or Azure evidence.
  End-to-end model/tool behavior remains unverified; citation validation
  stays enabled.
- Flex is configured at 512 MB with no always-ready instances. Microsoft
  recommends 2,048 MB for most scenarios and supplies an additional unbilled
  272 MB host buffer. Memory exhaustion has not been demonstrated; no memory
  or SKU increase was made without user approval. The user subsequently
  approved a bounded 2,048 MB test, including restart and manual trigger sync.
  Host checks still failed; 512 MB was restored and verified in ARM.
- A fresh runtime-catalog check exposed a configuration mismatch: display
  version `10` maps to ARM `functionAppConfig.runtime.version: 10.0`, while
  Bicep and the live app used `10`. Bicep and the deployment preflight were
  corrected to use/check the catalog's ARM value. Correcting the live value
  immediately produced a Running host at 512 MB; manual trigger sync succeeded.
- Both functions then reported `%Indexing__Schedule%` could not be resolved.
  Replaced this normalized environment name with flat `IndexingSchedule` in
  both timer attributes, Bicep, local settings and probe metadata. Published
  metadata contains both corrected timers, and invocation is accepted (202).
- First ingestion hit Search 503 rate-limit errors. Batched Search writes per
  committed source batch instead of per record; all Search actions finish
  before record states are persisted. Partial write/state failures and replay
  are covered by passing tests. The baseline uses three Search writer calls.
- Fixed deployment verification to decode binary checkpoint HTTP bodies.
  The actual SDK-created blob uses binary response content; string-only mocks
  had not covered that case. Both text and binary parser tests now pass.
- Hosted ingestion succeeded in 6.854 seconds. Search measured 176 documents,
  208,922 bytes (below 25 MiB), manifest sequence 5 and checkpoint sequence 5,
  with lease unlocked/available. Two bounded read-only verification grants
  were journaled and removed; the temporary-grant journal is empty.
- Model dependency telemetry then proved repeated HTTP 429 responses. Live
  capacity 1 allowed only 1 request / 1,000 tokens per minute, insufficient
  for the grounded prompt plus 2,000-token completion budget. The user approved
  capacity 50; ARM verifies 50 requests / 50,000 tokens per minute, same
  GPT-5 nano version, EU DataZoneStandard and NoAutoUpgrade policy.
- A subsequent five-second model response still returned invalid citation IDs.
  Added strict evidence-bound JSON schemas, refreshed on every model/tool
  round, while retaining independent server citation checks. With no knowledge,
  model citations must be null and are normalized to an empty API source list.
  Fake HTTP tests verify strict serialization, real tool invocation and schema
  refresh; 35 backend and 21 indexing tests pass. Updated backend is published.
- Removed all three exact journaled temporary operator grants after stopping
  deployment. Verified an empty temporary-grant journal and no remaining
  resource-scoped operator grants in the owned group. Runtime identity roles,
  source data and unrelated resources were left intact.
- Live acceptance completed on 2026-09-30. A real Claims Workbench sign-in
  question initially returned four validated knowledge sources without
  warnings. The final low-reasoning knowledge-only check returned five
  matching sources, zero Azure evidence and no warnings in 4.482 seconds,
  preserving the ordinary helpdesk path without unnecessary Azure calls.
- Fixed prompt ordering so retrieved untrusted evidence precedes the current
  request; a regression test preserves the bounded history and final-user
  ordering. Added tool descriptions and clarified that authorized read-only
  requests need no redundant confirmation.
- Hosted MCP Resource Health returned a real scoped observation with
  availabilityState Unknown. This is valid on F1, not measured HTTP uptime.
- Minimal reasoning still skipped tools on a mixed question. A bounded low
  reasoning test invoked MCP and returned five validated knowledge sources
  plus live metrics. Retained low in hosted settings and Bicep; model, SKU,
  quota and 2,000-token completion budget are unchanged. Reasoning tokens are
  billed, so average token usage can increase.
- Raw bucket arrays exposed an arithmetic error in the model answer. Metrics
  now have server-computed sample totals and observed/missing bucket counts.
  Final hosted mixed test (26.865 seconds) correctly reported 50 Requests,
  2 Http5xx, 24 observed buckets and zero missing buckets for each metric,
  five knowledge sources, one scoped Azure evidence item and no warnings.
- Protected source inspection returned 200 with employee authentication and
  401 without it. Write requests were refused and an unmapped Claims Workbench
  question received no fabricated Azure evidence.
- Optional probes remain disabled. The availability request returned an
  explicit unavailable-evidence warning and unknown uptime, not 100 percent.
  This is not a successful measured-availability or probe-coverage claim.
- Timer logs show successful no-change runs at 13:00 and 13:15 UTC, completing
  in 2,090 ms and 834 ms respectively after the verified sequence-5 ingestion.
- Employee token renewal was verified through the normal frontend Send
  question flow after expiry; no tokens were copied or exposed to the user.
- Latest validation: 43 backend tests, Bicep compilation, editor diagnostics
  and PowerShell safety tests pass. The 21 indexing tests passed after the
  earlier ingestion fixes; indexing code is unchanged in this final phase.
- Backend published with the same pinned MCP checksum and executable ZIP
  permissions. Hosted liveness is 200, low reasoning is verified, and the
  temporary-role journal is empty. No paid hosting upgrade or memory increase
  was retained. Demo end-to-end knowledge and live health/metrics flows are
  verified; this is not production readiness or an uptime SLA.

### Live role verification

Commands: `az webapp identity show`, `az functionapp identity show`,
`az identity show`, followed by `az role assignment list
--assignee-object-id <principal> --all`.

- Backend: Search Index Data Reader on Search, Key Vault Secrets User on its
  vault, Monitoring Metrics Publisher on Insights.
- Indexer: Search Index Data Contributor on Search, Storage Blob Data Owner,
  Queue Data Contributor and Storage Account Contributor on its storage
  account, Monitoring Metrics Publisher on Insights.
- Dedicated MCP: subscription Reader and Log Analytics Reader on its dedicated
  workspace; no writer or secret data roles observed.
- Status: live assignments match the plan. Hosted knowledge reads/indexing
  and MCP Resource Health/metric calls verified functional identity use.
  Optional measured-availability probes remain disabled.
