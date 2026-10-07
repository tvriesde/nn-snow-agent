# Local helpdesk agent evaluations

This application runs the **actual .NET `HelpdeskAgent.RunAsync`** with the
production prompt, Microsoft Agent Framework tool loop, evidence-bound answer
schema, employee-only Search filter, top-five keyword retrieval and citation
ledger. It scores the persisted answers with **azure-ai-evaluation 1.15.0**
locally. It does not supply an `azure_ai_project` or upload evaluation results
to Foundry. Candidate inference, Search and judge inference still call Azure
and can incur charges; "local" does not mean offline.

The browser dashboard is a **local, self-contained HTML file**. It contains
model/category comparisons and an expandable answer/evidence drill-down.
Nothing is published to the hosted employee frontend.

## Prerequisites and setup (PowerShell)

Use the repository's .NET 10 SDK and Python 3.10+ (verified on Python 3.14).

```powershell
az login
az account set --subscription d860292c-5d2c-4df3-b7c8-332bd46882d1
python -m venv evals\.venv
evals\.venv\Scripts\python.exe -m pip install -e .\evals
dotnet build evals\runner\Helpdesk.Evaluation.Runner.csproj
```

The operator must have Search Index Data Reader on the demo Search service
and Key Vault Secrets User on **only** the `azure-openai-key` secret. These
two explicitly approved roles are part of [the additive template](../infra/eval-models.bicep).
Model calls use the same API-key path as the hosted application. The key is
resolved at runtime from Key Vault and passed to the runner only through its
child-process environment; it is never included in config files or reports.
`azure_environment()` rejects a different CLI subscription or tenant.

## Deployments

[config.json](config.json) owns the local model catalog, separate from the
hosted backend's allowlist. Deploying evaluation models does not change the
application's model selection or its default.

| Candidate ID | Deployment | API | Reasoning |
|---|---|---|---|
| gpt-5-nano | helpdesk-mini (existing) | Chat Completions | low |
| gpt-6-luna | helpdesk-luna (existing) | Responses | low |
| gpt-5-mini | eval-gpt-5-mini | Chat Completions | low |
| gpt-4-1-mini | eval-gpt-4-1-mini | Chat Completions | none |
| gpt-4-1 | eval-gpt-4-1 | Chat Completions | none |

The independent scoring deployment `eval-judge` is GPT-5. The SDK's supported
`is_reasoning_model=True` option is required: without it the evaluators send
unsupported `max_tokens`/temperature parameters. This is configured explicitly.
Ground-truth reviewers use GPT-4.1 and GPT-5 mini, rather than grading a
candidate against an answer that the candidate itself generated.

The four new deployments use Sweden Central EU DataZoneStandard, pinned
versions, capacity 10 and `NoAutoUpgrade`. They and the approved roles were
provisioned on 2026-10-06. All model endpoints passed real inference checks.

```powershell
# Validation only: ownership guard, ARM validation, additive-only what-if.
.\scripts\deploy-eval-models.ps1
# Provision only after the documented Azure validation workflow/approval.
.\scripts\deploy-eval-models.ps1 -Deploy
```

No resources are deleted and modifications are refused by this script. To
resize, replace or remove deployments, use a separately approved deployment.
The small capacities mean full runs may take substantial time and encounter
provider throttling; the SDK/OpenAI clients retry throttling explicitly.

## Ground truth and confidence

[ground_truth.jsonl](datasets/ground_truth.jsonl) contains **50** items:
38 answerable, 6 missing-information questions and 6 safety/policy questions.
The baseline contains 176 eligible employee documents but only **12 distinct
synthetic scenarios**. This dataset evaluates that bounded corpus, not broad
production knowledge.

Each answerable reference is constructed from **exact source sentences**.
It includes required facts, forbidden claims, equivalent KB/incident/problem
IDs, and the actual source excerpts. Repeated KB copies and historical
resolutions are accepted as equivalent when they support every required fact.
Change/CMDB records and unresolved incidents are excluded by the real
`ServiceNowProjection`, not a separate Python reimplementation.

Validation gates:

1. Hash-verify the seed manifest and export documents using the .NET projection.
2. Validate 38/6/6 counts, unique IDs/fact sets and question near-duplicates.
3. Require each reference to contain only its selected source sentences.
4. Require complete equivalent-source sets, exact evidence and no unintended
   cross-application collisions.
5. Verify all support against the **live index** and run the application's
   exact search for every query. Search misses are reported, never removed.
6. Two distinct models independently answer every question **without the
   reference**, then check its support, completeness, ambiguity and extra claims.
   Strict per-item JSON schemas prevent incomplete batch reviews from passing.
7. A named human approves the generated
   [quality report](datasets/ground_truth.report.md). Approval binds the dataset,
   review and report hashes. Approval was supplied explicitly in this session.

Both independent models passed all 50 references after three narrow references
were clarified and re-reviewed. The live evidence gates passed for all 176
documents. All 41 source-backed cases retrieved a matching source in the top
five; the nine remaining cases use missing-information/system-policy rubrics.
Model agreement is corroboration, not mathematical proof or a guarantee of
production coverage.

```powershell
# Rebuilding drafts/metadata does not approve new content.
evals\.venv\Scripts\helpdesk-eval.exe build-dataset
evals\.venv\Scripts\helpdesk-eval.exe validate
evals\.venv\Scripts\helpdesk-eval.exe validate --live --review
# Inspect the report first; do not automate human approval in CI.
evals\.venv\Scripts\helpdesk-eval.exe approve --reviewer "Your name"
```

Every run checks the live corpus **before and after** execution and refuses
drift. Re-review after changing seed/index contents, questions or system
policy. Relevant evidence changes cannot be hidden by updating a fingerprint.

## Run, score and view

```powershell
# Balanced smoke run (15 responses: 3 categories x 5 candidates).
$run = 'C:\code\nn-snow-agent\evals\results\my-smoke-run'
evals\.venv\Scripts\helpdesk-eval.exe run --ids GT-001 GT-042 GT-045 --repeats 1 --output $run
evals\.venv\Scripts\helpdesk-eval.exe score $run
evals\.venv\Scripts\helpdesk-eval.exe compare $run --output "$run\report"
Start-Process "$run\report\comparison.html"

# Full workload: 50 questions x 3 repeats x 5 candidates = 750 responses.
# Judge calls are additional; this is not run automatically during setup.
$full = 'C:\code\nn-snow-agent\evals\results\full-run'
evals\.venv\Scripts\helpdesk-eval.exe run --repeats 3 --output $full
evals\.venv\Scripts\helpdesk-eval.exe score $full
evals\.venv\Scripts\helpdesk-eval.exe compare $full --output "$full\report"
Start-Process "$full\report\comparison.html"

# Run selected models.
evals\.venv\Scripts\helpdesk-eval.exe run --models gpt-5-mini gpt-4-1 --repeats 3
```

Output directories must be new: previous responses are never overwritten.
Candidate execution is sequential to preserve production retrieval behavior
and protect the Free Search tier. No deterministic inference seed is claimed;
repeats measure model variability. Every question gets a fresh conversation.

Azure health/investigation tools retain their real definitions but use explicit
unavailable implementations for this static-knowledge dataset. Calls are
recorded and scored as unexpected, not fabricated as successful. This
workload does **not** evaluate live Azure health, uptime or multi-turn memory.

## Metrics and interpretation

- **Groundedness**: claims versus the *actually retrieved* context, not the
  oracle's reference documents. Only answerable questions receive this score.
- **Relevance and intent resolution**: answerable cases only. Their generic
  SDK rubrics reward literal request fulfillment and can penalize correct
  abstention or safe refusal; missing-information/safety cases use the
  expected-behavior rubric instead. Inapplicable evaluations make no judge calls.
- **Completeness, similarity and F1**: references and final employee-facing
  answers. F1 is lexical overlap, not a correctness gate. Its SDK pass threshold
  can fail a correct, verbose paraphrase; the dashboard labels this distinction.
- **Retrieval**: SDK context quality plus exact initial hit@5 and hit after
  tool re-search. Do not confuse good retrieval with a good final answer.
- **Intent/task/tool-call accuracy**: actual tool definitions and captured
  model rounds. Task adherence receives the original system prompt and
  **raw model JSON**, not the server's plain-text rendering. Its SDK adapter
  keeps pre-retrieved evidence and the question together because SDK 1.15
  retains only the last user message during task grading.
- **Citation validity/correctness**: check both raw and server-filtered IDs.
- **Fact recall, forbidden claims and correct behavior**: a separate
  question-specific judge rubric, including refusal and missing details.
  Server-owned citations are supplied separately; inline citations are not required.
  Grounded-answer behavior is a deterministic gate over complete fact recall,
  valid supporting citations and no forbidden claims. The judge does not
  reinterpret the server's plain-text UI rendering as invalid model JSON.
- **Operational failures**: explicit failed rows; model/search warnings,
  timeouts and invalid responses cannot earn semantic passes.
  A successful search with zero matches is not an operational error: it is
  evaluated as a retrieval miss or an appropriate no-knowledge answer.
- **Latency/tokens/cost**: production `ProcessingTracker` metadata.
  Missing usage/cached-write counts and unverified pricing are N/A, never zero.
  Newly deployed model prices are not guessed. Estimates exclude judge
  inference, Search, hosting, taxes and negotiated discounts.

SDK 1-5 metrics use their default pass threshold of 3; task adherence is
binary (**1 = pass; 0 = fail**). HTML/CSV/Markdown report averages, descriptive
standard deviations, scoring coverage and pass denominators. For negative
flags (forbidden claim/unexpected tool), a false flag is a pass. Report
breakdowns include answerable, unanswerable, safety and retrieval-hard rows.
Empty subgroups are omitted entirely, per model. Retrieval-hard means the
initial top five missed all expected sources, not an authored difficulty label.
Different dataset/workload/judge/SDK configurations cannot be compared as
though they were identical experiments.

`scores.json` is the authoritative per-model result file; missing SDK numeric
values are normalized to JSON `null`, not non-standard NaN. Re-score an
affected evaluator without paying to repeat all other judgments:

```powershell
evals\.venv\Scripts\helpdesk-eval.exe score $run --metrics task rubric
evals\.venv\Scripts\helpdesk-eval.exe compare $run --output "$run\report"
```

Results and the virtual environment are git-ignored. Reports contain synthetic
questions, source excerpts and local observations, so share them deliberately.

### Dashboard and saved-result validation

The dashboard has model, category, metric, coverage-status and text filters.
Its top-of-page guide explains question categories, metric families, score
scales and coverage statuses, including why some measurements are inapplicable.
Overall results are selected by default; inapplicable metrics are hidden
unless explicitly enabled. Coverage shows scored/eligible and total cases,
distinguishing expected exclusions from genuine missing scores and failures.

One chart per eligible metric compares **all models**, with common per-metric
scales, raw means, score direction and sample sizes. Category/metric filters
apply to charts; the model filter only narrows the table/drill-down. Unavailable
models have no bar, never a synthetic zero. Charts with no eligible cases for
any model are skipped with an explanation. Cost bars include only verified estimates.

Every comparison persists `validation.json` and displays its checks: workload
matching, approved dataset/review hashes, saved before/after corpus fingerprints,
exact retrieved excerpts, and deterministic score/trace consistency.
Validation errors appear explicitly and cause the command to fail. This audit
is not another independent semantic judge; limited samples cannot establish
a model ranking. Item drill-down includes the expected answer/facts and citations.

Adapter version 4 scopes relevance/intent to answerable cases. Selective
rescoring also excludes earlier inappropriate judgments, retaining their score,
outcome and explanation in `excluded_scores` and the original SDK artifacts.
No repeat judge calls are needed to apply this applicability correction:

```powershell
evals\.venv\Scripts\helpdesk-eval.exe score $run --metrics code
evals\.venv\Scripts\helpdesk-eval.exe compare $run --output "$run\report"
```

## Tests

```powershell
evals\.venv\Scripts\python.exe -m unittest discover -s evals\tests -v
dotnet test tests\evals\Helpdesk.Evaluation.Tests.csproj
dotnet test tests\backend\Helpdesk.Backend.Tests.csproj --filter "FullyQualifiedName~ModelSelectionAndUsageTests|FullyQualifiedName~ApiTests|FullyQualifiedName~PolicyTests"
```

CI runs offline tests and source gates without Azure credentials. Live reviews
and model benchmarks are intentional operator actions.
