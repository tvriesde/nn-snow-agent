---
name: azure-health-model-state
description: 'Get the evaluated health state of any application from its Azure Monitor health model using only Azure MCP Server tools. Resolves an application tag, custom tag, health model name, or resource ID. WHEN: "health of application", "is my app healthy", "health model status", "health state of <tag/app>", "health of health model <name>", "Azure Monitor health model".'
---

# Azure Monitor Health Model State (Azure MCP only)

## Goal

Return the evaluated health state shown on the health model's Overview page in the Azure portal: Healthy, Degraded, Unhealthy, or Unknown.

## Hard rule: Azure MCP Server only

Use only Azure MCP Server tools for every Azure lookup. In VS Code these tools are typically named `Azure-MCP-*`, or have a server prefix such as `mcp_azure_mcp_*`.

Do not use:

- Azure CLI, including `az rest`
- Azure PowerShell or direct ARM, REST, or HTTP calls
- Azure tools that look similar but do not come from the Azure MCP Server. For example, `azure_query_azure_resource_graph` comes from another VS Code extension and is not allowed.
- The Azure portal or browser automation
- Previous chat results, cached health, or the historical example in this file

If Azure MCP cannot provide the required data, stop and report health as **Unknown**. Name the missing Azure MCP command or error. Do not fall back to another Azure access method.

Local actions that do not query Azure are allowed, such as searching a saved Azure MCP `learn` output file or checking the installed extension version.

## Critical: use the right health source

| Field / source | Meaning | Use? |
|---|---|---|
| `results.healthModel.healthState` from Azure MCP `monitor_healthmodels_get` | Evaluated application health | YES |
| `provisioningState` | Deployment status only | NO |
| Generic health model resource properties, such as `group_resource_list` or a generic ARM resource read | Resource metadata. The resource body has `provisioningState`, not `healthState`. | NO |
| Azure MCP `resourcehealth` tools | Infrastructure availability of individual resources | NO |
| Resource existence, absence of alerts, or empty results | Not a health signal | NO |

Never infer health from provisioning success, absence of alerts, or empty query results. A missing `healthState` in a generic resource payload does not mean that health is Unknown. Only `monitor_healthmodels_get` is authoritative.

## Prerequisites

- VS Code extension **Azure MCP Server** (`ms-azuretools.vscode-azure-mcp-server`) version 3 or later. Its `monitor` namespace must expose `monitor_healthmodels_list` and `monitor_healthmodels_get`.
- Signed in to Azure through the Azure MCP credential chain, such as the VS Code Azure account or Azure CLI login. The identity needs Reader access to the health model.

### Version compatibility (verified)

| Extension version | Bundled Azure MCP Server | Health model commands | Result |
|---|---|---|---|
| 2.0.46 | 2.0.5 | `monitor_healthmodels_entity_get` only; `entity` is required | Not usable. It cannot list or get models. Guessed entities (`root` and the model name) returned HTTP 400. |
| 3.0.47 | 3.0.0-beta.47 | `monitor_healthmodels_list`, `monitor_healthmodels_get` | Works. It returns tags and the evaluated `healthState`. |

### Upgrade Azure MCP Server

1. Open the Extensions view in VS Code and select **Azure MCP Server**.
2. Update to version 3 or later. If the update is not offered, install the latest release or pre-release.
3. Run **Developer: Reload Window**, or restart the server from **MCP: List Servers**.
4. Start a new chat so the agent rediscovers the tools.
5. Run `monitor` with `learn: true` and confirm that `monitor_healthmodels_get` is listed.

Older extension folders may remain on disk after an update. Use the `learn` output, not folder listings, to determine the active command set.

## Tool discovery and invocation

Do not invent tool or command names.

1. In VS Code, Azure MCP tools are named `Azure-MCP-*`, for example `Azure-MCP-monitor` and `Azure-MCP-subscription_list`. Other hosts may use a prefix such as `mcp_azure_mcp_*`.
2. If tools are deferred, search for `Azure-MCP-monitor` and `Azure-MCP-subscription_list` before calling them.
3. Call `monitor` with `{ "intent": "discover Azure Monitor health model commands", "learn": true }`.
4. The `learn` output is large, about 27 KB, and may be saved to a file. Search it for `healthmodels` instead of reading it all. Version 3 lists each command with `command` and `inputSchema` fields.

Azure MCP exposes commands in one of two modes. Use the mode that is available:

- **Namespace mode, used by the VS Code extension:** call the `monitor` router:

  ```json
  {
    "intent": "get health state of health model <name>",
    "command": "monitor_healthmodels_get",
    "parameters": { "subscription": "<id>", "resource-group": "<rg>", "health-model": "<name>" }
  }
  ```

- **Individual-tool mode:** call `monitor_healthmodels_get` directly with the same parameters.

If `learn` does not list both `monitor_healthmodels_list` and `monitor_healthmodels_get`, stop. Report health as Unknown, and explain that the Azure MCP Server must be upgraded.

### Command reference (Azure MCP Server 3.0.0-beta.47)

| Command | Required parameters | Optional parameters | Returns |
|---|---|---|---|
| `monitor_healthmodels_list` | None | `subscription`, `resource-group`, `tenant` | `results.healthModels[]` with `id`, `name`, `resourceGroup`, `location`, and `provisioningState`. It does not return tags or health. |
| `monitor_healthmodels_get` | `health-model`, `resource-group` | `subscription`, `tenant` | `results.healthModel` with `healthState`, `tags`, `id`, `name`, `resourceGroup`, `location`, `provisioningState`, and `identity` |

Always pass `subscription` explicitly. Otherwise, Azure MCP uses the default subscription from the CLI profile or `AZURE_SUBSCRIPTION_ID`.

## Workflow

### 1. Resolve the subscription

If the user supplied a subscription, use only that subscription. Otherwise, call Azure MCP `subscription_list` and search the subscription where `isDefault` is true first. If no match is found there, search the other enabled subscriptions.

### 2. Resolve the user's input to a health model

This workflow is application-agnostic. Never assume an application, model, resource group, or subscription from the historical example.

Classify the input first:

| User input | Example | Resolution |
|---|---|---|
| Application name | `contoso-orders` | Use tag key `application` and the supplied value. |
| Explicit tag | `app=contoso-orders` or `costcenter: 1234` | Use the supplied tag key and value. |
| Health model name | `orders-health` | Match `name` in `monitor_healthmodels_list`. |
| Health model name and resource group | `orders-health` in `orders-rg` | Call `monitor_healthmodels_get` directly. |
| Health model resource ID | `/subscriptions/<id>/resourceGroups/<rg>/providers/Microsoft.CloudHealth/healthmodels/<name>` | Parse the subscription, resource group, and name, then call `monitor_healthmodels_get`. |

Health models are `Microsoft.CloudHealth/healthmodels` resources. They are often, but not always, tagged `application=<app-name>`.

#### Find a model by tag

1. Call Azure MCP `monitor_healthmodels_list` with `subscription`. Add `resource-group` if the user supplied one.
2. For each returned model, call Azure MCP `monitor_healthmodels_get` with `subscription`, `resource-group`, and `health-model`. The list result does not include tags. Independent `get` calls can run in parallel.
3. Match against `results.healthModel.tags`:
   - Match the tag key case-insensitively, because Azure tag names are case-insensitive. For example, `application`, `Application`, and `APPLICATION` are the same key.
   - Match the tag value case-insensitively after trimming whitespace. Report the stored value exactly as returned.
   - Do not use partial or fuzzy matching for tag values.
4. Repeat for remaining subscriptions as described in step 1.

#### Find a model by name

1. Call `monitor_healthmodels_list` and match `name` case-insensitively and exactly.
2. Use the returned `resourceGroup` to call `monitor_healthmodels_get`.

#### Ambiguous or missing results

- **One match:** continue to step 3.
- **Multiple matches:** list each name, resource group, and subscription. Ask the user which model to inspect.
- **No tag match, but a model name contains the application name:** list those models as unconfirmed candidates. Show their actual tags and ask the user to confirm. Do not report a candidate's health as the application's health until the user confirms it.
- **No match:** report that Azure MCP found no matching health model. List the subscriptions and the tag key and value that were searched. Health cannot be determined. Do not reuse another application's result.
- **`monitor_healthmodels_get` returns 404 `ResourceNotFound`:** report that the health model does not exist in that resource group. Do not report a health state.

### 3. Get the evaluated health state

Use the fresh `monitor_healthmodels_get` result from step 2 for the selected model. If step 2 did not call `get` for that model, call it now. Do not call another tool for health.

Read `results.healthModel.healthState`:

- `Healthy`: all signals are within thresholds.
- `Degraded`: some signals have crossed warning thresholds.
- `Unhealthy`: critical thresholds have been crossed.
- `Unknown`, `null`, or missing from this response: health cannot be determined. Report Unknown; do not guess.

### 4. Optional per-entity breakdown

Azure MCP Server 3.0.0-beta.47 and the locally verified 3.0.0-beta.49 expose health-model `list` and `get`, but no entity-health or failing-signal command. This beta.49 command set was checked with local MCP help on October 2, 2026, without querying Azure. Server 2.0.5 exposed `monitor_healthmodels_entity_get`, but it required an exact entity name and had no command to list entities.

1. Run `monitor` with `learn: true` and check for entity list or entity health commands.
2. Use an entity command only when Azure MCP returns the entity names or the user provides them.
3. Never guess entity names.

If Azure MCP cannot provide component-level health, say that this breakdown is unavailable through Azure MCP. Do not use CLI, REST, or another tool as a fallback.

A Degraded or Unhealthy overall state does not identify which service contributed
to that state, and does not prove a root cause. Do not label every service as
degraded. Resource Health and metric symptoms are not substitutes for evaluated
component health or confirmed contributing signals.

### 5. Report in two layers

Lead with a short, plain-language answer: the evaluated state, what that means
for the employee, and the UTC check time. For Degraded/Unhealthy, explain which
components/signals Azure MCP actually returned, or explicitly say that the
installed server cannot identify the contributing service. Never invent an
explanation to make the answer feel complete.

Keep subscription IDs, command names, matched tags and deployment metadata in
the expandable evidence details, rather than leading with a technical dump.
Keep the state and uncertainty server-owned; readable prose must not override
the evaluated state.

Include in the technical evidence:

- Application or tag searched, the matching tag as stored, and whether `monitor_healthmodels_get` verified it
- Health model name, resource group, and subscription name and ID
- **Health state**, shown prominently
- Provisioning state, labeled as deployment status only
- Source: Azure MCP `monitor_healthmodels_get`
- Azure MCP tools and commands used
- Retrieval time

### 6. Reduce avoidable latency in a hosted agent

- For unambiguous, standalone health questions, a host may route directly to
  this skill without a knowledge search or model-generation round. Mixed
  questions, unclear application names and context-dependent follow-ups must
  retain normal agent routing.
- Reuse the hosted MCP connection/process, but always retrieve fresh evaluated
  health. Never cache or reuse a previous health state.
- Reuse a fresh `get` result from resolution; do not retrieve it a second time.
- An exact model name plus resource group avoids list/discovery, when that is
  what the user actually supplied. Do not guess a model to skip discovery or
  silently skip ambiguity checks.
- Record skill duration and MCP call count to distinguish lookup time from
  model/search overhead. Free-tier host cold starts and Azure response times
  remain outside the skill's control.

## Troubleshooting (Azure MCP only)

| Symptom | Cause | Action |
|---|---|---|
| `learn` shows only `monitor_healthmodels_entity_get`, or no health model commands | Azure MCP Server is older than version 3 | Report Unknown and ask the user to upgrade the extension. See [Upgrade Azure MCP Server](#upgrade-azure-mcp-server). |
| `monitor_healthmodels_entity_get` returns HTTP 400 | The entity name is invalid | Stop. Do not retry with guessed names. |
| `Azure-MCP-*` tools are not callable | The tools are deferred or the server is stopped | Search for the tool. If it is still missing, ask the user to start or restart Azure MCP Server. |
| An authentication or authorization error occurs | No credential or no Reader access | Ask the user to sign in or grant Reader access, then retry through Azure MCP. |
| `healthState` is `null` | Health has not been evaluated or is not available | Report Unknown. Do not infer it from `provisioningState`. |
| `monitor_healthmodels_get` returns 404 `ResourceNotFound` | The model name or resource group is wrong | Report "not found." Use `monitor_healthmodels_list` to find the correct name and resource group. |
| No model has the requested tag | The model is untagged, uses another tag key, or does not exist | Show unconfirmed name-based candidates, if any, and ask the user. Otherwise report "not found." |

## Historical example

Tag: `application=employee-it-helpdesk`

Model: `snowdemo-health`, resource group `snowdemo-rg`, subscription `Tyrone-Subscription-CreditCard` (`d860292c-5d2c-4df3-b7c8-332bd46882d1`)

Environment: VS Code Azure MCP Server extension 3.0.47, with Azure MCP Server 3.0.0-beta.47

Azure MCP calls used:

1. `Azure-MCP-subscription_list`
2. `Azure-MCP-monitor` with `learn: true`
3. `Azure-MCP-monitor` command `monitor_healthmodels_list` with `subscription`
4. `Azure-MCP-monitor` command `monitor_healthmodels_get` with `subscription`, `resource-group`, and `health-model`

On October 2, 2026, `monitor_healthmodels_get` returned `healthState: "Healthy"`, `provisioningState: "Succeeded"`, and tag `application: "employee-it-helpdesk"`.

This is an example only. Always re-query through Azure MCP; never reuse cached health.

## Validated behavior for other applications

These scenarios were validated on October 2, 2026, with Azure MCP Server 3.0.0-beta.47. The subscription contained one health model.

| Input | Azure MCP evidence | Expected outcome |
|---|---|---|
| `contoso-payments` | `list` returned one model. `get` showed `application=employee-it-helpdesk`, which does not match. | Not found; no health state reported |
| `EMPLOYEE-IT-HELPDESK` | The tag value matched case-insensitively. | The matched model's `healthState` |
| Health model `contoso-payments-health` in a known resource group | `get` returned 404 `ResourceNotFound`. | Model not found; no health state reported |
| Application plus resource group | `list` with `resource-group` returned only models in that group. | The search is limited to that resource group |
