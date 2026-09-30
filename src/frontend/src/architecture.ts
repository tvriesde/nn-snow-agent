export const requestFlow = [
  { title: 'Employee browser', detail: 'React Native Web / Expo on Linux App Service F1. Microsoft Entra sign-in uses PKCE; employee tokens go only to the helpdesk API.' },
  { title: '.NET agent backend', detail: 'ASP.NET Core on the shared F1 App Service plan validates tenant, audience and Helpdesk.Access scope. Microsoft Agent Framework orchestrates the agent in this backend, not in a hosted Foundry agent service.' },
  { title: 'Two evidence paths', detail: 'Azure AI Search retrieves employee-visible ServiceNow-shaped knowledge. The pinned Azure MCP subprocess investigates only explicitly mapped live applications using read-only tools.' },
  { title: 'Azure GPT deployment', detail: 'GPT-5 nano generates guidance from retrieved evidence and tool results. The backend uses a server-only API key resolved through Key Vault; no model key reaches the browser.' },
  { title: 'Verified response envelope', detail: 'The server checks citation IDs against its current evidence ledger and returns answer, knowledgeSources, azureEvidence and warnings. Sources and Azure observations are server-owned, not invented by the model.' },
] as const;

export const indexingFlow = [
  { title: 'Synthetic ServiceNow export', detail: 'Table API-shaped JSON contains knowledge articles, incidents, known problems, change records and business-application records. This is dummy insurer data, not a connection to a real ServiceNow instance.' },
  { title: 'Private Blob Storage', detail: 'Standard LRS storage holds source batches and a committed manifest. The manifest identifies ordered batches and their hashes; a separate state container holds versions and the checkpoint.' },
  { title: 'Scheduled Azure Function', detail: 'A .NET isolated Function runs every 15 minutes on Flex Consumption, at 512 MB with no always-ready instances. It reads only new committed sequences after the checkpoint.' },
  { title: 'Safe delta indexing', detail: 'Hash/sequence checks and a renewable Blob lease protect processing. Batched Search upserts/deletes finish before record versions and the checkpoint are advanced. Replays are idempotent; a no-change tick writes no Search documents.' },
  { title: 'Employee knowledge index', detail: 'Published employee KBs, sanitized resolved-incident notes and known-problem resolutions enter the Free Search index. Unpublication/deletion removes affected documents. Raw work notes, caller details, change records and CMDB inventories are not employee knowledge.' },
] as const;

export const agentSettings = [
  { label: 'Agent', value: 'EmployeeHelpdesk', detail: 'Microsoft Agent Framework with a bounded FunctionInvokingChatClient and per-request evidence ledger.' },
  { label: 'Model', value: 'GPT-5 nano · 2025-08-07', detail: 'Deployment helpdesk-mini, EU DataZoneStandard. Resource in Sweden Central; inference may occur elsewhere within the EU. No automatic model upgrade.' },
  { label: 'Inference', value: 'Low reasoning · 2,000 completion tokens per model round', detail: 'Azure OpenAI /openai/v1/ Chat Completions. The completion budget includes reasoning; no temperature override. Tokens are billed by usage, not by assigned throughput quota.' },
  { label: 'Knowledge retrieval', value: 'servicenow-knowledge · top 5 matches', detail: "Simple keyword search with visibility eq 'employee'. No embeddings or paid semantic ranking. Knowledge is prefetched before the first model call; further tool retrieval refreshes allowed citation IDs." },
  { label: 'Tool limits', value: '4 model/tool-loop iterations · 6 tool calls including prefetch', detail: 'One consecutive tool error allowed. Function invocation is sequential; MCP access is also serialized. The agent cannot execute arbitrary commands or KQL.' },
  { label: 'Conversation', value: 'Latest 4 turns in the prompt · 12-turn conversation limit', detail: 'Employee-isolated, in-memory conversations reset on backend restart. Retrieved evidence precedes the current question and is treated as untrusted data, not instructions.' },
  { label: 'Timeouts', value: '75-second chat deadline · 25-second MCP operation timeout', detail: 'Dependency failures and missing evidence produce visible warnings. They never become fabricated sources, telemetry or successful actions.' },
  { label: 'Response guard', value: 'Strict JSON schema + independent citation validation', detail: 'The answer schema permits only current retrieved source IDs. With no knowledge, citations are null and normalized to an empty list. Invalid citations are withheld; citation checks are not a guarantee that every sentence is correct.' },
] as const;

export const agentTools = [
  { name: 'SearchKnowledge', operation: 'Knowledge', detail: 'Find published employee guidance and real source IDs in Azure AI Search. The model can request further retrieval, but cannot write to the index.' },
  { name: 'InvestigateAzure', operation: 'metrics', command: 'monitor_metrics_query', detail: 'Requests and Http5xx over the last 24 hours. Totals are computed server-side from observed buckets; missing buckets are not zero. These counts are not HTTP uptime.' },
  { name: 'InvestigateAzure', operation: 'availability', command: 'monitor_workspace_log_query', detail: 'Executed HTTP probe samples from the dedicated workspace. Optional probes are currently disabled: measured uptime is unknown, not 100 percent.' },
  { name: 'InvestigateAzure', operation: 'activity', command: 'monitor_activitylog_list', detail: 'At most 10 recent, resource-scoped activity events over 24 hours. Temporal proximity to an error does not prove that a deployment caused it.' },
  { name: 'InvestigateAzure', operation: 'health', command: 'resourcehealth_availability-status_get', detail: 'Current Azure platform Resource Health. Unknown is valid on Free hosting; platform health is not an HTTP availability test.' },
] as const;

export const applicationAliases = [
  'Employee IT Helpdesk backend',
  'Employee IT Helpdesk frontend',
] as const;

export const identityBoundaries = [
  { title: 'Backend system identity', detail: 'Search Index Data Reader, Key Vault Secrets User on its vault and Monitoring Metrics Publisher on demo Insights. Model authentication uses the server-side Key Vault-resolved key.' },
  { title: 'Dedicated MCP identity', detail: 'Subscription Reader and Log Analytics Reader only on the demo workspace. A sanitized child environment selects this user-assigned identity, with no developer/CLI credential fallback or inherited model key.' },
  { title: 'Indexer system identity', detail: 'Search Index Data Contributor and storage Blob Data Owner, Queue Data Contributor and Storage Account Contributor for identity-based Function hosting. Shared account-wide storage roles are not hard container isolation.' },
  { title: 'Application policy', detail: 'Exact tool allowlist, fixed subscription/resource mappings and server-generated arguments. Restarts, authentication changes, secret retrieval, arbitrary queries and writes are refused. A subprocess is not a hard OS security boundary.' },
] as const;
