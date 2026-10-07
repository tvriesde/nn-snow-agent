from __future__ import annotations

from dataclasses import dataclass
from typing import Any


ANSWERABLE_METRICS = {"groundedness", "retrieval", "relevance", "intent_resolution"}


@dataclass(frozen=True)
class Metric:
    label: str
    maximum: float | None = 1
    lower_is_better: bool = False
    note: str = ""


METRICS = {
    "groundedness.groundedness": Metric("Groundedness", 5, note="Answerable cases only; claims versus retrieved evidence."),
    "relevance.relevance": Metric("Relevance", 5, note="Answerable cases only; refusal and abstention use behavior rubrics."),
    "completeness.response_completeness": Metric("Response completeness", 5, note="Coverage relative to the expected answer."),
    "similarity.similarity": Metric("Semantic similarity", 5),
    "f1.f1_score": Metric("Lexical F1", note="Word overlap, not a correctness gate. Verbose paraphrases can score poorly."),
    "retrieval.retrieval": Metric("Retrieval quality", 5, note="Answerable cases only."),
    "intent.intent_resolution": Metric("Intent resolution", 5, note="Answerable cases only; does not reward unsafe requests."),
    "task.task_adherence": Metric("Task adherence", note="Binary: 1 = pass, 0 = fail; grades actual model JSON and policy."),
    "tools.tool_call_accuracy": Metric("Tool-call accuracy", 5, note="Only model-requested tool calls; server pre-search is not a tool call."),
    "code.operational_success": Metric("Operational success"),
    "code.citation_validity": Metric("Citation validity", note="Raw and server-owned citations must reference retrieved sources."),
    "code.citation_correctness": Metric("Supporting citations", note="Requires an expected supporting source."),
    "code.retrieval_hit_at_5": Metric("Initial retrieval hit@5", note="Requires an expected supporting source."),
    "code.retrieval_hit_after_tools": Metric("Retrieval hit after all searches", note="Includes initial search and any model-requested re-search."),
    "code.unexpected_tool_call": Metric("Unexpected tool calls", lower_is_better=True, note="0 = no unexpected calls."),
    "rubric.required_fact_recall": Metric("Required-fact recall", note="Only cases with required facts."),
    "rubric.behavior_correctness": Metric("Expected behavior", note="Grounded answer, correct abstention, or safe refusal as required."),
    "rubric.forbidden_claim": Metric("Forbidden claims", lower_is_better=True, note="0 = no forbidden claim."),
    "elapsed_ms": Metric("Response latency (ms)", None, True, "Server-measured latency; descriptive sample only."),
    "tokens_total": Metric("Candidate tokens", None, True, "All candidate model rounds; excludes judge tokens."),
    "model_token_cost_usd": Metric("Candidate token estimate (USD)", None, True, "Only verified prices and complete billing counts; excludes judge/services."),
}


def applicability_reason(row: dict[str, Any], key: str) -> str | None:
    metric = key.removeprefix("outputs.")
    name = metric.split(".")[-1]
    if name in ANSWERABLE_METRICS and row.get("inputs.category") != "answerable":
        return "Use the expected-behavior rubric for missing information or safety; literal request fulfillment is not the goal."
    prefix = key.rsplit(".", 1)[0]
    if row.get(f"{prefix}.{name}_applicable") is False:
        return row.get(f"{prefix}.{name}_reason") or "Evaluator marked this case not applicable."
    item = row.get("inputs.item", {})
    trace = row.get("inputs.row", {})
    if name == "tool_call_accuracy" and not trace.get("tool_calls"):
        return "No model-requested tool calls; initial Search is server-driven."
    if name in ("citation_correctness", "retrieval_hit_at_5", "retrieval_hit_after_tools") and not item.get("expected_source_ids"):
        return "No expected supporting source for this case."
    if name == "required_fact_recall" and not item.get("required_facts"):
        return "No required facts; evaluate abstention/refusal with the behavior rubric."
    return None


def result_key(key: str) -> str:
    return "outputs.f1.f1_result" if key == "outputs.f1.f1_score" else key + "_result"
