# Architecture Decision Records

This document records consequential architecture decisions for the employee IT
helpdesk, distinguishes deployed choices from choices still requiring product
or operator approval, and gives each open decision a concrete way to resolve
it. An option described here is not approved unless its ADR status says
**Accepted**.

- **Accepted**: decision has been made and is reflected in the deployed design.
- **Open**: no final decision; do not implement a materially different option
  without review and approval.
- **Proposed**: recommended direction, still awaiting approval.
- **Superseded**: retained for history, but no longer the active decision.

For deployment coordinates, test evidence and full operating limitations see
the [deployment plan](./.azure/deployment-plan.md). Do not add credentials,
access tokens, private endpoint URLs, or unnecessary tenant, subscription,
principal, or resource identifiers here.

## Decision index

| ID | Decision | Status |
| --- | --- | --- |
| ADR-001 | Browser-first React Native application and minimum-cost demo hosting | Accepted |
| ADR-002 | Employee authentication and managed-identity separation | Accepted |
| ADR-003 | ServiceNow-shaped synthetic source and committed-batch delta indexing | Accepted |
| ADR-004 | Lexical-only Azure AI Search retrieval for the initial release | Accepted |
| ADR-005 | Microsoft Agent Framework with bounded, read-only Azure MCP tools | Accepted |
| ADR-006 | GPT-5 nano in the EU Data Zone with token-based billing | Accepted |
| ADR-007 | Evidence provenance and explicit availability limitations | Accepted |
| ADR-008 | Evaluate vector/hybrid retrieval | Open |
| ADR-009 | Define additional live Azure resource scope and resource types | Open |
| ADR-010 | Decide whether measured HTTP probes are required | Open |
| ADR-011 | Choose the production availability and hosting target | Open |
| ADR-012 | Connect a real ServiceNow source and set its data-handling rules | Open |
| ADR-013 | Decide conversation persistence and retention | Open |
| ADR-014 | Set enforceable operating and model-cost budgets | Open |
| ADR-015 | Define how Azure investigations determine application health | Open |
| ADR-016 | Choose indexer optimizations from measured workload and freshness requirements | Open |
| ADR-017 | Choose Search strategy and distinguish Foundry dependencies | Open |

## Accepted decisions

### ADR-001: Browser-first React Native application and minimum-cost demo hosting

- **Status:** Accepted
- **Date:** 2026-09-30
- **Context:** The requested employee experience is a React Native app, while
  the requested host is Azure App Service.
- **Decision:** Use Expo and React Native Web to export a browser SPA; App
  Service does not host an iOS or Android binary. Host the frontend and .NET
  backend in separate apps sharing a Linux Free F1 plan. Run indexing on Azure
  Functions Flex Consumption at 512 MB without always-ready instances.
- **Consequences:** This keeps the demo at minimum hosting cost but accepts
  cold starts, F1 CPU quotas, idle shutdown and no App Service SLA. It is not a
  production availability commitment.

### ADR-002: Employee authentication and managed-identity separation

- **Status:** Accepted
- **Date:** 2026-09-30
- **Context:** Employees need authenticated helpdesk access, while the agent
  needs to read Search knowledge, invoke the model and inspect Azure resources.
- **Decision:** Use single-tenant Microsoft Entra SPA and API registrations,
  PKCE, and a delegated API scope. The backend validates employee tenant,
  audience and scope. Use separate system-assigned identities for the backend
  and indexer, and a dedicated user-assigned identity for Azure MCP.
- **Consequences:** Browser code contains no client secret or model key. MCP
  uses a sanitized managed-identity environment and has read-only Reader access
  plus workspace-scoped Log Analytics Reader. Subscription visibility does
  not itself authorize every resource for agent investigation.

### ADR-003: Synthetic source and committed-batch delta indexing

- **Status:** Accepted
- **Date:** 2026-09-30
- **Context:** A working ServiceNow-shaped ingestion demo was needed without
  credentials or access to a production ServiceNow instance.
- **Decision:** Store synthetic Table API-shaped records in private Blob
  Storage. A scheduled .NET isolated Function reads immutable batches only
  after a hash- and sequence-checked manifest commits them. It batches Search
  writes and advances record state and the checkpoint only after writes
  succeed; replay is idempotent.
- **Consequences:** This demonstrates delta and recovery semantics but is not
  a production ServiceNow connector. Work notes, caller data, raw change
  records and CMDB inventory are not published as employee knowledge.

#### Current indexer mechanics (implementation reference, 2026-10-02)

This is a custom **push indexer**, not an Azure AI Search managed indexer.
The [.NET timer Function](./src/indexer/IndexKnowledgeFunction.cs) runs every
15 minutes on Flex Consumption, with 512 MB instances, no always-ready
instances and a five-minute Function timeout. Its default limit is five
committed batches per invocation; the engine accepts a configured limit of
1-50. Increasing the Function app's instance ceiling does not parallelize this
feed because one renewable checkpoint lease serializes processing.

1. **Read committed input:** The producer uploads immutable snapshot/delta
   payloads before updating `manifest.json`. The
   [source reader](./src/indexer/BlobSourceReader.cs) checks each source blob's
   size (maximum 4 MB) and downloads it into memory. The
   [delta engine](./src/core/DeltaIndexer.cs) validates manifest schema,
   contiguous sequences starting at 1, unique safe paths and SHA-256 hashes.
   A batch descriptor permits at most 1,000 source records. Snapshot batches
   contain `result` records; delta batches contain explicit upserts/deletes.
   A snapshot is not an authoritative full reconciliation: a record omitted
   from a later snapshot is not automatically deleted.
2. **Acquire exclusive progress state:**
   [Blob-backed state](./src/indexer/BlobIndexState.cs) creates the checkpoint
   only if absent, acquires a 60-second lease and renews it every 20 seconds.
   Lease contention logs a warning and skips that tick. Renewal failure logs
   an error and cancels work; checkpoint writes require the lease ID.
3. **Process after the checkpoint:** Read at most the configured number of
   subsequent committed batches, in order. Verify each payload hash and its
   declared record count; reject duplicate `(table, sys_id)` keys in a batch.
   Read each record's Blob state, which stores its sequence version and prior
   chunk IDs. A record already committed at this sequence is skipped. State
   ahead of the sequence is an error. Sequence, not `sys_updated_on`, orders
   changes, so equal timestamps do not lose updates.
4. **Project only eligible employee knowledge:** The
   [projection](./src/core/ServiceNowProjection.cs) includes employee-audience,
   published KB content unless explicitly inactive, resolved/closed incident
   close notes, and known-error problem resolutions. It strips script/style
   blocks, HTML tags and redundant whitespace. It excludes raw work notes,
   caller fields, change records and CMDB inventory. This allowlisted
   projection is not a general personal-data redaction service: text in an
   allowed field still needs an approved data policy for a real source.
5. **Build deterministic chunks and removals:** Split plain text into chunks
   of at most 1,800 characters without splitting UTF-16 surrogate pairs.
   IDs are `table_sysid_chunk-number`; there is no overlap, token-aware or
   section-aware splitting, embedding, OCR or semantic enrichment. Compare
   new IDs with prior IDs to remove obsolete chunks. Explicit deletes,
   unpublication and audience changes remove previously indexed chunks.
6. **Write Search before progress:** Collect actions for the batch in memory.
   The [Search writer](./src/indexer/SearchKnowledgeWriter.cs) uploads full
   documents, then submits deletes, in sequential pages of 100 actions. It
   inspects every action result, retries only failed actions for selected
   transient statuses, and allows three total attempts with 2- and 4-second
   delays. Permanent/exhausted item failures throw; request-level failures
   also propagate (SDK transport retries are separate). Only after all Search
   actions succeed does the engine persist each record state, then advance
   the batch checkpoint. There is no transaction spanning Search and Blob.
7. **Recover by replay:** A partial Search failure leaves record state and
   checkpoint unchanged for that batch; retrying deterministic IDs is
   idempotent. A partial record-state failure skips records whose state was
   saved and replays the remaining records. If only checkpoint persistence
   fails, replay can advance it without rewriting committed records. Malformed
   data blocks progress and is logged, not silently skipped or dead-lettered.
   No new sequences means **zero Search writes**, not zero Function/Storage
   work: the tick still acquires a lease and reads/validates manifest/state.

The [indexing tests](./tests/indexer/DeltaIndexerTests.cs) cover no-change runs,
partial Search/state/checkpoint failures, same-timestamp updates, shrinking
articles, removal/visibility changes, corrupt hashes and bounded resume.
Current throughput costs include sequential per-record state reads/writes,
full reuploads of eligible changed records, whole-manifest validation and
in-memory batch/chunk lists. These are measurement targets, not evidence that
the small deployed corpus already has a performance problem.

### ADR-004: Lexical-only Azure AI Search retrieval for the initial release

- **Status:** Accepted
- **Date:** 2026-09-30
- **Context:** The initial demo prioritizes low cost and a small corpus. Azure
  AI Search is on its Free tier.
- **Decision:** Index searchable text and filterable metadata; retrieve the top
  five employee-visible matches using simple keyword search. Do not create
  document or query embeddings, vector fields, or a semantic ranker in the
  initial release.
- **Consequences:** Exact names and codes are inexpensive to search. Paraphrase
  recall may be weaker. Hybrid retrieval is a separate, open evaluation in
  ADR-008, not an enabled feature.

### ADR-005: Microsoft Agent Framework and guarded Azure MCP

- **Status:** Accepted
- **Date:** 2026-09-30
- **Context:** Answers need both indexed employee guidance and live, read-only
  Azure evidence, without giving the model general Azure command authority.
- **Decision:** Run Microsoft Agent Framework in the .NET backend. Expose
  `SearchKnowledge` and `InvestigateAzure`. Run the pinned Azure MCP preview
  release over stdio in read-only mode, with proxy tools disabled and an exact
  native-tool allowlist. The backend, not the model, chooses authorized
  resources and constructs each request's arguments.
- **Consequences:** Investigations are limited to explicitly configured
  aliases and operations. Writes, restarts, secrets, arbitrary KQL and generic
  command execution are prohibited. A subprocess is not a hard OS isolation
  boundary.

- **Version update (2026-10-02):** Source/deployment packaging now pins Azure
  MCP `3.0.0-beta.49`, published on 2026-10-01, with fixed Linux archive and
  executable SHA-256 checks and a same-binary NuGet fallback. The exact four
  allowed tools and their argument schemas passed offline MCP SDK contract
  tests against the matching Windows release. This is a beta dependency and
  a   source-only update at that point, not a production certification.
  The subsequent approved application-health skill deployment uses beta.49
  and expands the exact allowlist to six tools by adding health-model list/get.
  Reverify Linux hosted managed-identity queries at deployment.
  [Official release](https://github.com/microsoft/mcp/releases/tag/Azure.Mcp.Server-3.0.0-beta.49).

### ADR-006: GPT-5 nano EU Data Zone with token-based billing

- **Status:** Accepted
- **Date:** 2026-09-30
- **Context:** The originally considered model failed the Azure deployment
  preflight; EU processing and low demo cost were required.
- **Decision:** Use GPT-5 nano version `2025-08-07` on DataZoneStandard, with
  automatic upgrades disabled. Use the Azure OpenAI v1 endpoint and low
  reasoning. Bound each model round to 2,000 completion tokens. The configured
  capacity of 50 is quota allocation, not provisioned throughput.
- **Consequences:** Inference can occur within the EU Data Zone, not
  necessarily in Sweden Central. Billing remains per token; reasoning tokens
  are billed. A fixed completion budget and deployment capacity do not provide
  a monthly cost ceiling.

- **2026-10-02 extension:** Add GPT-6 luna `2026-09-22` as `helpdesk-luna`,
  EU DataZoneStandard capacity 10, without replacing GPT-5 nano. Keep nano as
  default; the authenticated UI selects an allowlisted model per question and
  retains conversation history. Both use low reasoning and the same tool,
  strict evidence-schema and completion-budget boundaries.
  Live compatibility requires Chat Completions for nano and Responses for
  luna: luna rejects Chat Completions tools with low reasoning. The user chose
  Responses rather than disabling reasoning. Use the pinned SDK adapter with
  `store=false`; retain application-owned history and no silent API fallback.
  Report server-agent processing time and complete provider token counts from
  every round, withholding partial totals. Cached input/reasoning are subsets,
  not extra tokens. Standalone health questions bypass both models.
  Configure official Azure Retail API Data Zone USD rates (checked 2026-10-02):
  nano input/cached/output 0.055/0.0055/0.44 per million; luna short-context
  0.12/0.012/0.60 plus cache-write 0.15. Optional estimates cover model tokens
  only and use a conservative 20,000-input-token per-call guard, not a claimed
  provider tier boundary. Missing usage, cached-input breakdown or applicable
  pricing suppresses an estimate. Luna cache-write counts are unavailable in
  the verified SDK, so its estimate is withheld rather than assuming no writes.

### ADR-007: Evidence provenance and explicit availability limitations

- **Status:** Accepted
- **Date:** 2026-09-30
- **Context:** Helpdesk guidance, Resource Health, request metrics and
  end-to-end HTTP availability are different kinds of evidence.
- **Decision:** Keep synthetic knowledge citations separate from timestamped
  Azure observations. Enforce model response schemas against the server's
  evidence ledger and independently reject citations not retrieved for the
  current answer. Compute metric totals server-side and retain missing-sample
  counts. Never treat a running resource state or request count as HTTP uptime.
- **Consequences:** Optional HTTP availability probes are disabled. The
  deployed demo reports measured uptime as unknown rather than claiming
  continuous coverage or 100 percent availability.

## Open decisions

### ADR-008: Evaluate vector or hybrid Search retrieval

- **Status:** Open
- **Date raised:** 2026-10-02
- **Context:** The deployed retriever uses only lexical Search. Embeddings may
  improve recall for employee paraphrases but introduce embedding-generation,
  vector-storage, ranking and cost decisions. Azure hybrid search combines
  text and vector retrieval; it requires vectors for its vector leg.
- **Decision question:** Should production helpdesk retrieval combine keyword
  and vector search, or remain keyword-only?
- **Option A - Keep keyword-only (current deployment):**
  - **Pros:** No embedding deployment or embedding calls; no vector field,
    profile or vector-index storage; simple incremental indexing and
    reindexing; lower moving-part count and easier exact-term debugging. Strong
    fit when employees use canonical application names, KB terms, error codes
    and ticket identifiers.
  - **Cons:** May miss useful articles when the employee describes the
    symptom in substantially different words; depends more on lexical overlap,
    query terms and indexing synonyms.
- **Option B - Hybrid keyword + vector retrieval:**
  - **Pros:** Combines lexical matches for exact codes/names with semantic
    similarity for paraphrases and natural-language symptom descriptions.
    Search can run the text and vector legs together and merge rankings using
    Reciprocal Rank Fusion. It can improve recall without discarding exact
    matches.
  - **Cons:** Requires document embeddings and a query embedding for each
    vector search (generated in the application/indexer or by an approved
    integrated vectorization path); adds model selection, token cost,
    latency, dimensions/profile configuration, vector storage, synchronization
    on every insert/update/delete, and relevance tuning. The deployed Free
    Search service's capacity and feature limits must be verified against the
    chosen vector configuration. Hybrid ranking can add candidates that look
    semantically similar but are not the right policy or application; it does
    not guarantee better answers and still needs citation and relevance tests.
- **Option C - Vector-only retrieval:**
  - **Pros:** Can retrieve conceptually related wording when lexical overlap
    is low.
  - **Cons:** Gives up the complementary exact-term ranking path and can rank
    exact error codes, product names, KB numbers or identifiers less
    predictably. For this support corpus, prefer evaluating hybrid rather than
    replacing keyword search with vectors alone.
- **Scenario fit:**

  | Employee query scenario | Likely fit | Why / what to verify |
  | --- | --- | --- |
  | “PRB0030002”, HTTP 503, exact app name, or a specific error code | Keyword or hybrid | Exact lexical matches matter; verify vector results do not displace the exact record. |
  | “Policy Administration sends me around a login loop” versus an article titled “Repeated SSO redirects and login loops” | Hybrid | Semantic similarity may bridge differences in symptom wording. Measure whether the known problem is retrieved in the top five. |
  | “I changed phones and now I’m locked out of the second verification step” versus “MFA prompts go to an old phone” | Hybrid | Employee paraphrase and conceptual match are useful; ensure it does not retrieve unsafe bypass guidance. |
  | Query includes two distinct issues or a named business application | Keyword or hybrid | The result must remain application-relevant; test for cross-application semantic false positives. |
  | Rare acronyms, identifiers, names, or newly added terminology | Keyword first; hybrid as a complement | Embeddings may not preserve exact identifiers reliably; lexical retrieval remains important. |
  | No article should answer the question, or the only match is from the wrong application | Either, with a no-match test | More semantic matches can increase false positives; enforce a retrieval relevance threshold or safe abstention behavior. |
  | Corpus and query wording already overlap closely and top-five results are correct | Keyword-only may suffice | If a representative benchmark shows no material hybrid gain, avoid the additional cost and operational surface. |
- **Proposed direction (not yet approved):** Evaluate **hybrid** retrieval as a
  candidate, retaining keyword retrieval as a signal. Do not change the
  deployed index or enable vector queries until the benchmark, Free-tier
  capacity/feature check, embedding model/region and cost are reviewed.
- **Decision and implementation questions if Option B is selected:**
  1. Which embedding model, dimensions and processing region are approved?
  2. Should the indexer call the embedding model, or should Search use
     integrated vectorization? Where are credentials and model access scoped?
  3. How will source updates and deletes atomically/reliably keep text and
     vectors consistent with the existing sequence/checkpoint design?
  4. Does the deployed Free Search service support the selected vector
     configuration and corpus, or is an explicitly approved tier/cost change
     required?
  5. Which retrieval evaluation set, top-k relevance target, latency budget
     and monthly embedding budget constitute a pass?
- **Recommended evaluation before deciding:** Use a separate evaluation index;
  preserve the deployed lexical index. Test a labelled set of exact-code,
  application-name, paraphrase, MFA, login-loop, multi-issue and no-answer
  questions. Compare keyword-only against hybrid at top 5. Record recall of
  relevant articles, wrong-application/unsafe false positives, citation
  correctness, abstention, p50/p95 latency, embedding/indexing cost and vector
  storage. Then record an Accepted ADR choosing yes or no.

### ADR-009: Define additional live Azure resource scope and resource types

- **Status:** Open
- **Date raised:** 2026-09-30
- **Context:** The MCP identity can read within its Azure RBAC scope, but the
  application intentionally accepts only explicitly mapped aliases. The
  current policy accepts `Microsoft.Web/sites` App Services and exposes four
  fixed read operations. A user has identified `swa-pulseo-prod-weu`, an Azure
  Static Web App, which is not one of the configured helpdesk aliases.
- **Decision needed:**
  1. Should that specific Static Web App be added as an approved target?
  2. Should the policy support `Microsoft.Web/staticSites` as a separate
     resource type, rather than broadening discovery to arbitrary resources?
  3. Which Static Web Apps metrics, activity and platform health signals are
     required? What identifier and subscription/group restrictions apply?
  4. Does the MCP native tool set support those signals? If not, which fixed,
     read-only Azure API/SDK integration is approved instead?
  5. Is live HTTP probing required, and what privacy, region, interval, and
     cost constraints apply?
- **Consequences of no decision:** The agent must continue to refuse or report
  no authorized mapping for the Static Web App. Subscription-level Reader
  permission alone is not justification for unrestricted model-directed
  resource discovery.
- **Approval required:** Owner of the target application and Azure subscription
  must approve exact resource scope and tool operations before code or RBAC
  changes.

### ADR-010: Decide whether measured HTTP probes are required

- **Status:** Open
- **Date raised:** 2026-09-30
- **Context:** Resource Health and platform request metrics are not end-to-end
  checks that an employee can reach a URL. The optional scheduled probe is
  currently disabled and uptime is unknown.
- **Decision needed:** Whether probes are necessary for the demo or production;
  which approved URLs and network vantage points to test; frequency, timeout,
  acceptable success threshold, result retention and alert recipients; and
  whether waking Free F1 apps and consuming compute/telemetry is acceptable.
- **Recommended next step:** Define a small monitored target set and sampling
  and alerting requirements. Estimate the Functions, telemetry and target-app
  CPU impact. Keep probes disabled until the target owner approves those
  parameters. Report sample coverage and unknown intervals with any resulting
  availability calculation. ADR-015 separately defines what those observations
  mean when answering "is it healthy?"

### ADR-011: Choose the production hosting and reliability target

- **Status:** Open
- **Date raised:** 2026-09-30
- **Context:** Free F1 and Azure AI Search Free are demo choices; they have
  material quota, capacity, feature, and availability limitations.
- **Decision needed:** Intended users and request volume; required availability,
  latency, recovery time, scale and data residency; frontend/backend plan,
  Search tier and any redundancy; and the approved monthly spend.
- **Consequences of no decision:** The current architecture remains a demo.
  Do not describe it as production-ready or silently upgrade its paid SKUs.

### ADR-012: Connect a real ServiceNow source and define data handling

- **Status:** Open
- **Date raised:** 2026-09-30
- **Context:** The deployed blob-feed contract and records are synthetic; they
  do not authenticate to or synchronize from an actual ServiceNow instance.
- **Decision needed:** ServiceNow tables and query scope; API authentication
  and secret ownership; fields allowed into the employee index; exclusion or
  redaction of personal, policy, claim and internal-only details; publication
  and deletion semantics; ingestion frequency; retry and audit requirements;
  and data-residency/retention controls.
- **Consequence:** Real ServiceNow access must not be introduced by merely
  substituting credentials into the synthetic-data producer.

### ADR-013: Decide conversation persistence and retention

- **Status:** Open
- **Date raised:** 2026-09-30
- **Context:** The backend stores bounded conversations in process memory.
  Conversations are employee-isolated but disappear on restart or scale-out
  and do not provide durable conversation history.
- **Decision needed:** Whether cross-request/restart continuity is needed; if
  so, storage service, tenant/user partitioning, encryption, retention/deletion
  period, operational access and whether chat transcripts may be stored at
  all.
- **Consequence:** Keep the existing ephemeral store until an approved
  retention and privacy policy exists; do not persist prompts or responses by
  default.

### ADR-014: Set enforceable operating and model-cost budgets

- **Status:** Open
- **Date raised:** 2026-09-30
- **Context:** Token limits, Search top-k, Function free grants and a telemetry
  daily cap reduce usage but do not cap the total Azure invoice.
- **Decision needed:** Monthly and alert thresholds; allowed employee traffic;
  token/request/concurrency budgets; model and telemetry budget ownership;
  handling when limits are approached; and whether subscription spending
  alerts or budget automation are required.
- **Consequence:** Until a budget owner approves limits, do not claim the
  current deployment has a guaranteed maximum monthly cost.

### ADR-015: Define how Azure investigations determine application health

- **Status:** Accepted for evaluated health-model reporting; HTTP journey/SLA policy remains open
- **Date raised:** 2026-10-02
- **Decision (2026-10-02):** Package the exact
  [azure-health-model-state skill](.github/skills/azure-health-model-state/SKILL.md)
  with the backend and load its source-selection, resolution and reporting
  workflow into the Microsoft Agent Framework agent. Application-health
  questions invoke `GetApplicationHealth`. Its guarded adapter uses only
  Azure MCP `monitor_healthmodels_list` and `monitor_healthmodels_get`;
  `results.healthModel.healthState` is the authoritative evaluated state.
  `InvestigateAzure` remains for infrastructure evidence and is not a
  substitute for application health.
- **Hosted adaptation:** Use the configured subscription and dedicated MCP
  identity, not employees' default subscriptions or VS Code sign-in.
  `Azure:HealthApplication` maps "this helpdesk" to its actual application tag.
  Exact case-insensitive application/custom-tag matching, model-name and
  resource-ID resolution are supported. No matching model yields an explicit
  Unknown/not-found report; multiple matches require user selection and
  name-only candidates remain unconfirmed. Direct get failures never reuse
  another model. Null, missing or unrecognized evaluated states are Unknown.
  Provisioning state is labeled deployment status only.
- **Enforcement and bounds:** The published skill is loaded at startup;
  editor setup and historical examples are not agent instructions/evidence.
  The MCP allowlist expands from four to six read-only commands, with schema
  validation and explicit subscription arguments. Discovery is limited to
  20 models and 45 seconds; broader scans require a narrower query, never
  partial-success attribution. Each report includes source, matched tag/model,
  resource group, subscription, commands and retrieval time. Reports and Azure
  evidence are server-owned so the model cannot override the health state.
  No health-model write, added RBAC, REST/CLI fallback, cached observation or
  guessed entity lookup is introduced.
- **Remaining question:** Evaluated health reflects the health model's configured
  signals. This change does not establish HTTP uptime, SLA coverage or a
  successful authenticated employee journey. The approaches below remain
  options for separately improving those signals and the health model.
- **Readability/latency update (2026-10-02):** Lead with a short, plain-language
  answer and keep model/tag/subscription/command/provisioning metadata in
  expandable Azure evidence. The locally verified MCP beta.49 command set has
  no per-component-health or failing-signal command. A Degraded report cannot
  identify which service contributed or prove a root cause; the user explicitly
  chose to retain MCP-only health access instead of adding CloudHealth REST.
  Unambiguous standalone health questions route directly to the packaged skill,
  avoiding Search prefetch and OpenAI generation/tool-loop rounds. Mixed,
  ambiguous or context-dependent queries retain normal agent routing. Every
  request still resolves freshly, preserving ambiguity handling and preventing
  stale health reuse. Duration/MCP call count are logged; no paid hosting change
  or guaranteed latency claim is made.
- **Decision question:** What evidence and policy should determine whether a
  mapped application is healthy, degraded, unavailable or unknown *now*, and
  what should be reported about historical availability?
- **Infrastructure evidence path (retained, not an application-health classifier):**
  - The model chooses `InvestigateAzure(alias, operation)`. The backend
    resolves only exact configured aliases for `Microsoft.Web/sites` in the
    fixed subscription and constructs arguments for one of four read-only
    operations. It does **not** automatically run all four operations or
    evaluate a health rule. An unmapped app cannot be evaluated.
  - `health` calls Azure MCP's Resource Health availability-status tool for
    the mapped resource. The backend extracts `availabilityState`,
    `reasonType`, `occurredTime` and `reportedTime`, timestamps the
    observation, and labels it **Azure platform Resource Health**, not HTTP
    uptime. It requires a returned state but does not turn `Available` into
    "the application works" or `Unknown` into "down." Free F1 has returned
    `Unknown` in the deployed demo.
  - `metrics` requests 24 hourly buckets of App Service `Requests` and
    `Http5xx`. The backend sums returned samples and reports observed and
    missing bucket counts. There is no configured error-rate threshold,
    minimum request volume or traffic-free interpretation. Request/error
    counts alone cannot prove end-to-end availability.
  - `activity` retrieves recent resource-scoped management events. A nearby
    deployment may warrant investigation but does not prove the cause or
    health of an endpoint.
  - `availability` reads `AppAvailabilityResults` for the mapped resource
    from the dedicated workspace and rejects zero executed samples; it does
    not currently compute a coverage-qualified uptime percentage or a
    current-state verdict. A timer-based single-location HTTPS probe exists
    in code, but no probe targets are configured in the deployed Bicep, so
    measured uptime is **unknown**. A successful probe of a public endpoint
    would not prove an employee can authenticate or complete a workflow.
  - The model receives the selected evidence and is instructed not to
    confuse platform health, metrics or sampled probes with HTTP uptime.
    Its prose is not an independently validated health verdict. If MCP fails
    or the alias is unauthorized, the API returns no Azure observation and a
    warning; the present warning combines these distinct failure reasons.
- **Candidate approaches:**

  | Approach | What it can establish | Advantages | Limitations / risks |
  | --- | --- | --- | --- |
  | Keep platform signals and cautious narrative (current) | Whether Resource Health reports a platform status; whether request errors or management changes were observed | Read-only, no new probe traffic or paid monitoring; useful context for diagnosis | No reliable user-facing healthy/unhealthy verdict; `Unknown`, missing or zero traffic do not imply down or up |
  | Add approved unauthenticated HTTP probe(s) (ADR-010) | Reachability and response status of specific public URLs from named vantage points at sampled times | Directer signal for a URL; measurable sample count and latency | Health endpoint may be shallow; authentication, dependencies and employee journey remain untested; single vantage point, cold starts and missing schedules bias results; wakes F1 |
  | Add authenticated synthetic employee journey | Whether a test identity can sign in and complete explicitly chosen critical actions | Closest to the user experience; can distinguish broken login from a working health URL | More complexity, secrets/test-account governance, privacy, maintenance, tenant policy and cost; never use real customer data |
  | Combine approved signals with deterministic, per-app rules | Distinct platform state, endpoint reachability, dependency/error symptoms and coverage; a bounded current verdict | Explainable and testable; model summarizes rather than inventing thresholds or overriding missing data | Needs owners to define freshness windows, critical journeys, thresholds, precedence and exceptions; conflicting signals require `Degraded` or `Unknown`, not a confident "healthy" |

- **Scenarios the decision must cover:**

  | Observation | Safe interpretation until a policy is approved |
  | --- | --- |
  | Resource Health `Available`, public URL returns 503 | Platform reports available; the URL is failing. Do not declare the application healthy. |
  | Resource Health `Unknown` on Free F1, a recent HTTP check succeeds | Platform state is unknown; the checked URL was reachable at that time, not continuously up. |
  | Zero requests and zero 5xx in a bucket | Possibly no traffic or missing instrumentation; not proof of 100% uptime. |
  | HTTP 200 on `/health/live`, employee login fails | Process is alive; the employee journey is unhealthy. |
  | Probe fails once during a cold start but succeeds on retry | Report time, latency and both outcomes; decide consecutive-failure/degraded thresholds, not an immediate unqualified outage. |
  | No probes executed or the last observation is stale | Availability/current endpoint state is unknown, not healthy or down. |
  | Platform incident alongside failures | Correlation supports investigation, not automatic root-cause attribution. |
  | Unmapped resource or MCP permission/tool failure | No authorized/verified evidence. Never turn an access or tool error into a health state. |

- **Decision needed:** Define "health" separately for platform, public HTTP
  endpoint, authenticated employee journey and historical uptime. For each
  mapped application, approve target endpoints and ownership, critical
  dependencies, allowable probe identity/vantage points, observation interval,
  stale-after window, expected schedule and coverage floor, latency/error
  thresholds, consecutive-failure/recovery rules, handling for `Unknown` and
  conflicting signals, and alert recipients. Choose whether a composite
  verdict is needed at all; decide whether a sampled success rate can be
  presented as an SLO measure, never as a contractual SLA without an agreed
  definition and coverage. Coordinate probe enablement with ADR-010 and
  additional resource types with ADR-009.
- **Proposed direction (not approved):** Start with a per-resource,
  **deterministic evidence policy** returning separate platform and endpoint
  observations plus `Healthy`, `Degraded`, `Unhealthy` or `Unknown` and explicit
  reasons. Require fresh, sufficient coverage before a positive verdict;
  otherwise return `Unknown`. Keep historical sample success and missed
  intervals separate from "healthy now." Let the LLM explain this
  server-computed result, never decide a health threshold from free-form text.
- **Acceptance evidence before implementation:** Test the scenarios above with
  fixed timestamps and missing/contradictory samples; verify that unavailable
  MCP data, unmapped resources and absent probes do not yield success-shaped
  results. Measure cold-start effects, probe cost and sampling coverage in the
  cheapest approved environment before enabling alerts or making uptime
  claims.

### ADR-016: Choose indexer optimizations from measured workload and freshness requirements

- **Status:** Open
- **Date raised:** 2026-10-02
- **Context:** ADR-003's single ordered worker deliberately favors correctness
  and low idle cost. Ingestion throughput, knowledge freshness and retrieval
  relevance are different problems; tuning one does not necessarily improve
  the others. No new ingestion architecture is approved here.
- **Measurement gate:** Establish agreed targets for commit-to-searchable
  freshness (including unpublication/deletion), backlog age and drain time,
  p95 invocation duration, memory high-water mark, records/chunks per second,
  Blob transactions, Search throttle/failure rate and monthly ingestion cost.
  Current logs expose sequence and action counts, but not all these metrics;
  add bounded, privacy-safe telemetry before claiming an optimization helps.
  A 15-minute schedule is not a 15-minute freshness guarantee: cold starts,
  backlog, failures and Search visibility delays can add latency.
- **Candidate scenarios and selection criteria:**

  | Workload / observed problem | Candidate optimization | Pick when | Trade-offs and correctness conditions |
  | --- | --- | --- | --- |
  | Small, infrequently changing text corpus; current freshness is acceptable | Keep the timer, push writer and Blob checkpoint | Measured freshness/backlog and spend meet agreed targets | Lowest complexity; retain replay tests and explicit error reporting. Do not optimize solely because a larger production design exists. |
  | Backlog grows, but executions have time/memory headroom and Search is not throttling | Tune schedule and `Indexing:MaxBatches`; test moderately larger Search pages | More sequential work can drain backlog within the five-minute timeout and memory budget | More ticks increase idle transactions. Bound pages by payload bytes as well as action count; 1,000 records can create many more chunks. Raising the limit does not solve an oversized single batch. |
  | Runtime/memory, not schedule, limits progress | Split producer batches; stream parsing and bound action buffers; consider more Function memory after profiling | p95 duration approaches the timeout or chunk expansion exhausts 512 MB | Preserve all-or-replay semantics, prior IDs for deletes and record-state-after-Search ordering. Streaming flushes may partially write before later validation fails; validate input first or make that replay behavior explicit. More memory is a paid configuration choice. |
  | Most time is per-record Blob state I/O | Bounded concurrent state reads and safe state writes under the existing lease; consider a state store with bulk/conditional operations | Storage latency/transaction measurements dominate the invocation | Keep Search completion before state commit and await all state writes before checkpoint advancement. A cache is not durable truth. A new database adds cost/migration; packed state blobs need size/concurrency limits. |
  | Many source updates change only excluded fields, not indexed content | Store a projection fingerprint and schema/projection version; later use per-chunk content fingerprints | The avoidable upload/embedding ratio justifies extra state | Fingerprint searchable text **and** indexed metadata, eligibility and chunking version. Advance source progress even for unchanged projections; do not skip unpublication, deletes or schema changes. Define whether `sourceVersion`/`updatedAt` must update in Search even when text is unchanged. |
  | Search throttling or transient failures dominate | Bound writer concurrency, add jitter/backpressure and load-tested page sizing; assess Search capacity | Retrying/tight loops increase latency instead of useful throughput | More Function workers/memory will not fix Search capacity. Retry failed actions only, propagate exhausted failures, never advance progress on partial success; paid Search changes require approval (ADR-011/014). |
  | Freshness needs seconds/minutes, or traffic arrives in bursts | Queue/Event Grid-triggered wake-up after manifest commit; optionally Durable Functions for controlled orchestration | An agreed freshness target cannot tolerate scheduled polling | Events are duplicate/out-of-order notifications, not the ordering authority. Drain committed sequences under the lease, include poison-event handling and periodic reconciliation for missed notifications. Payload-created events before manifest commit must not index uncommitted data. |
  | Independent domains have sustained throughput beyond one worker | Partition feed, leases and checkpoints; scale queue workers per partition | A measured single-writer bottleneck remains after simpler tuning | Stable document ownership and ordering within each partition are mandatory; never let two partitions write the same document. Define barriers and reconciliation across partitions. Simply removing the global lease breaks correctness. |
  | Source is mostly supported files in Blob, not a custom ordered record feed | Evaluate a native Search indexer with index projections/skillsets | Managed extraction and enrichment reduce operating code and supported source semantics meet requirements | Not a drop-in reader of these manifest/delta JSON files. Materialize a suitable source representation and validate eligibility, chunk lifecycle, soft-delete/tombstone behavior and recovery. Keep one ingestion owner per target index. Foundry dependency depends on the skills, not on using a native indexer. |
  | Manifest/state history grows, or source and index can drift | Versioned manifest segments/compaction plus periodic reconciliation; shadow-index rebuild and controlled cutover | Manifest overhead, disaster recovery or schema/model migration warrants it | Current validation assumes a complete sequence list from 1, so compaction requires a new feed/checkpoint contract. Never delete replay history needed by a recovering consumer. Compare IDs/counts/content/visibility, not counts alone, and verify rollback storage and Search tier limits. |

- **Recommended order (not approval):** Baseline first, then choose the lowest
  complexity fix for the measured bottleneck. Try bounded batching/state I/O
  and projection deduplication before event-driven or partitioned processing.
  Choose a managed indexer for source/enrichment fit, not merely to use Foundry.
  Improve chunking only if retrieval evaluation shows a relevance problem.
- **Approval and acceptance:** Agree freshness and spend targets; run realistic
  long-record/backlog/throttle benchmarks and fault injection for partial
  writes, duplicate events, lost leases, restart and deletion. No silent
  poison-batch skip. Preserve the current no-change and replay invariants.
  Real source ingestion additionally requires ADR-012's data-handling approval.

### ADR-017: Choose Search strategy and distinguish Foundry dependencies

- **Status:** Open
- **Date raised / documentation checked:** 2026-10-02
- **Scope:** Complements ADR-004/008. Retrieval mode (keyword/vector/hybrid),
  ingestion owner (custom push/native indexer), model host and agent host are
  independent decisions. Selecting hybrid search does not require replacing
  the current .NET Agent Framework backend with Foundry Agent Service.
- **Terminology:** "Foundry required" is ambiguous. Distinguish (1) the
  **Foundry portal/project and hosted Agent Service**, (2) an **Azure OpenAI
  model deployment**, now documented as Azure OpenAI in Foundry Models, and
  (3) an **AIServices Foundry resource** used by built-in enrichment skills.
  Using an Azure OpenAI endpoint does not by itself require a Foundry project
  or hosted agent. The existing GPT deployment answers questions; it is not an
  embedding deployment. Existing capacity is not approval for new model usage.
- **Dependency and scenario matrix:**

  | Strategy / pipeline | Foundry project or hosted agent required? | Other requirements / Foundry-related dependency | When to choose |
  | --- | --- | --- | --- |
  | Keyword Search with current custom push ingestion; tune fields, analyzers, synonyms and scoring | **No** | Search only for retrieval; current GPT answer generation remains separate | Exact application names, KB IDs and error codes dominate; benchmark already has adequate recall. Lowest moving-part count. |
  | Keyword or hybrid **plus semantic ranker** | **No** | Search semantic configuration, supported region and billing allowance/plan; no customer-deployed embedding or chat model is needed for the ranker itself [1] | Relevant documents are retrieved but ordered poorly. It reranks candidates; it does not generate missing document vectors or repair missing source data. |
  | Vector-only or hybrid with embeddings generated by the custom indexer/app | **No** | Search vector fields/profiles and a compatible document/query embedding model. Azure OpenAI is optional: self-hosted or other approved model endpoints also work [2] | Prefer hybrid for paraphrased symptoms while retaining exact lexical matches. Keep the current checkpoint and citation controls; vector-only needs evidence it does not harm code/identifier retrieval. |
  | Native Search indexer + Text Split/custom preprocessing, without billable AI skills | **No** | Supported data source, skillset/index projections as needed; Text Split itself does not require an attached billable Foundry resource [3][4] | File-oriented ingestion where managed extraction/chunk mapping is preferable to custom record processing. Source/delete semantics must be redesigned and tested. |
  | Integrated vectorization using **Azure OpenAI embedding skill/vectorizer** | **No project or hosted agent required** | An Azure OpenAI resource **or** Foundry project with a supported embedding deployment; direct model billing and Search-to-model access [3][5] | Managed chunk/vector refresh with a supported data source. This is not model-free, and the current push Function is not automatically an integrated-vectorization pipeline. |
  | Integrated vectorization using custom Web API skill/vectorizer | **No** | Your approved embedding endpoint, matching indexing/query model space, auth, availability and hosting cost [3] | Need a non-Azure-OpenAI model or custom processing; accept ownership of scaling, retries and security. |
  | Foundry model-catalog embedding skill/vectorizer route | **Route-specific Foundry/model deployment setup; no hosted agent required** | The selected catalog deployment/endpoint and its skill/vectorizer requirements; catalog vectorizer support may be preview [3] | A catalog model meets tested language/domain/hosting needs better than Azure OpenAI or self-hosting. Do not assume all catalog models are interchangeable embeddings. |
  | Built-in OCR, entity/language extraction or translation enrichment at scale | **No project or hosted agent required** | Built-in Foundry Tools-backed skills need an attached billable **Foundry AIServices resource** beyond the limited free enrichment allowance; legacy supported resource kinds may differ [4] | Scans/images or multilingual/raw documents actually need enrichment. These dependencies do not apply to plain lexical/vector search. Custom preprocessing is an alternative. |
  | Azure AI Search knowledge-base / agentic retrieval APIs, minimal extractive mode | **No** | Search knowledge base + knowledge source(s); an index/semantic configuration for indexed sources; selected tier/API limits and retrieval billing [6] | Need a shared retrieval abstraction across approved sources without LLM query planning. A planner model is not required for minimal mode. |
  | Search agentic retrieval with low/medium reasoning or answer synthesis | **No hosted Foundry agent required** | Azure OpenAI model for LLM planning/synthesis, Search knowledge base/sources, retrieval and model billing; these capabilities are preview in the checked docs [6] | Multi-part questions need query decomposition/source selection and benchmarks justify added latency, cost and preview risk. Keep evidence validation in the app; do not conflate Search's answer synthesis with the current guarded answer envelope. |
  | Foundry IQ configured in Foundry portal and connected to Foundry Agent Service | **Yes, for this chosen portal/hosted-agent workflow** | Foundry project/agent, Search knowledge base/sources and selected model/identity/billing configuration [7] | Need Foundry's managed agent workflow and shared knowledge administration. The underlying Search knowledge-base APIs also work with Agent Framework/custom applications without a hosted Foundry agent. |

- **Tier and release caveats:** Check the live service's quotas, region, API
  version, SDK and identity/private-network support before selecting any row.
  Current docs state vector search is available on all tiers; vectors and
  embeddings still consume capacity and model budget [2]. Semantic ranker's
  free allowance is available on all pricing tiers, while its standard
  pay-as-you-go plan requires Basic or above [1]; the free allowance is not
  unlimited, nor a reason to silently upgrade this demo. This corrects any
  assumption that semantic ranking or vectors inherently need a paid tier.
  Current Search docs distinguish GA minimal extractive retrieval through
  `2026-04-01` from preview planning/synthesis/non-minimal reasoning through
  `2026-08-01-preview`; portal access remains preview-only [6]. Verify this
  again at implementation time rather than assuming all "agentic" features
  have the same release status.
- **Proposed selection sequence (not approved):** Keep keyword-only as the
  baseline. Test lexical tuning first; try semantic reranking for candidate
  ordering problems and hybrid for missing paraphrase matches. Use custom
  embeddings if preserving sequence/checkpoint ownership is important;
  evaluate integrated vectorization only alongside an ingestion redesign.
  Consider knowledge-base APIs for a genuine multi-source/shared-retrieval
  requirement and Foundry Agent Service only for a deliberate agent-hosting
  change, not as a prerequisite for Search.
- **Acceptance gate:** Compare top-five recall, ranking quality, wrong-app
  matches/no-answer behavior, citation accuracy, p95 query latency, ingestion
  freshness and total cost on exact-code, paraphrase, multi-issue and
  deletion/visibility scenarios. Hybrid/semantic results must retain
  employee filtering and server-owned citation IDs. Test text/vector update
  and deletion consistency, re-embedding/model/schema migration and rollback.
  Chunking/embedding fingerprints must include model, dimensions and pipeline
  version. Native indexers/Foundry do not automatically reproduce this app's
  visibility policy or provide tenant isolation. Agree regional processing,
  permissions, preview acceptance and budgets before provisioning or enabling.

#### Official references for ADR-017

Checked on 2026-10-02. Documentation and preview requirements can change.

1. [Semantic ranker enablement and billing](https://learn.microsoft.com/en-us/azure/search/semantic-how-to-enable-disable) and [semantic ranking overview](https://learn.microsoft.com/en-us/azure/search/semantic-search-overview).
2. [Vector search overview: external embeddings, hybrid queries and tier availability](https://learn.microsoft.com/en-us/azure/search/vector-search-overview).
3. [Integrated vectorization: built-in, custom and Foundry catalog paths](https://learn.microsoft.com/en-us/azure/search/vector-search-integrated-vectorization).
4. [Attach a Foundry resource to a skillset: billable skills and resource kinds](https://learn.microsoft.com/en-us/azure/search/cognitive-search-attach-cognitive-services).
5. [Azure OpenAI embedding skill prerequisites](https://learn.microsoft.com/en-us/azure/search/cognitive-search-skill-azure-openai-embedding).
6. [Agentic retrieval overview: optional models, API versions and billing](https://learn.microsoft.com/en-us/azure/search/agentic-retrieval-overview).
7. [Foundry IQ: portal/project workflow and custom application integration](https://learn.microsoft.com/en-us/azure/foundry/agents/concepts/what-is-foundry-iq).

## New ADR template

```text
### ADR-NNN: Short decision title
- Status: Proposed | Accepted | Superseded
- Date:
- Decision owner:
- Context:
- Decision:
- Alternatives considered:
- Consequences:
- Revisit when:
```
