from __future__ import annotations

from collections import defaultdict
import csv
import html
import json
import math
from pathlib import Path
import statistics
from typing import Any

from .common import DATASET, EVALS, corpus_hash, file_hash, read_json, read_rows, write_json
from .metrics import METRICS, applicability_reason, result_key


def metric_summary(rows: list[dict[str, Any]], key: str, *, lower_is_better: bool = False) -> dict[str, Any]:
    eligible = [row for row in rows if applicability_reason(row, key) is None]
    values = [float(row[key]) for row in eligible if isinstance(row.get(key), (int, float))
              and math.isfinite(row[key])]
    results = []
    missing = 0
    for row in eligible:
        outcome = row.get(result_key(key))
        if outcome in ("pass", "fail"):
            results.append(outcome)
        elif type(row.get(key)) is bool:
            results.append("pass" if row[key] != lower_is_better else "fail")
        if not isinstance(row.get(key), (int, float)) or not math.isfinite(row[key]):
            missing += outcome != "fail"
    status = ("empty" if not rows else "not_applicable" if not eligible else "missing" if missing
              else "failed" if not values else "partial" if len(values) < len(rows) else "scored")
    reasons = sorted({reason for row in rows if (reason := applicability_reason(row, key))})
    if missing:
        reasons.append(f"{missing} eligible response(s) have no numeric score; inspect the item-level evidence.")
    if len(values) < len(eligible) and "fail" in results:
        reasons.append("Failed executions remain in the pass denominator; no synthetic numeric score is assigned.")
    return {"mean": statistics.mean(values) if values else None, "std": statistics.pstdev(values) if values else None,
            "scored": len(values), "pass_rate": results.count("pass") / len(results) if results else None,
            "pass_denominator": len(results), "total": len(rows), "eligible": len(eligible),
            "not_applicable": len(rows) - len(eligible), "missing": missing, "status": status,
            "reason": " ".join(reasons)}


def sdk_input_equal(left: Any, right: Any) -> bool:
    if isinstance(left, dict) and isinstance(right, dict):
        return left.keys() == right.keys() and all(sdk_input_equal(left[key], right[key]) for key in left)
    if isinstance(left, list) and isinstance(right, list):
        return len(left) == len(right) and all(sdk_input_equal(a, b) for a, b in zip(left, right))
    if type(left) is bool or type(right) is bool:
        return type(left) is type(right) and left == right
    # SDK/Pandas JSON round-tripping changes the last bits of nested cost floats.
    if (isinstance(left, float) or isinstance(right, float)) and isinstance(left, (int, float)) and isinstance(right, (int, float)):
        return math.isclose(left, right, rel_tol=1e-12, abs_tol=0)
    return left == right


def formatted_mean(row: dict[str, Any]) -> str:
    if row["mean"] is None:
        return row["status"].replace("_", " ").capitalize()
    digits = 7 if row["metric"] == "model_token_cost_usd" else 3
    return f"{row['mean']:.{digits}f} +/- {row['std']:.{digits}f}"


def audit_model(responses: list[dict[str, Any]], results: list[dict[str, Any]],
                items: dict[str, dict[str, Any]], documents: dict[str, dict[str, Any]]) -> list[str]:
    from .scoring import code_metrics, grounded_behavior

    issues = []
    by_key = {(row["id"], row["repeat"]): row for row in responses}
    for result in results:
        response = by_key[result["inputs.id"], result["inputs.repeat"]]
        item = items[response["id"]]
        if not sdk_input_equal(result.get("inputs.row"), response) or result.get("inputs.item") != item:
            issues.append(f"{response['id']}: scored inputs differ from the saved response/reference.")
        checks = code_metrics(response, item)
        for key, value in checks.items():
            if result.get("outputs.code." + key) != value:
                issues.append(f"{response['id']}: code.{key} disagrees with the captured trace.")
        for source in response["retrieved_sources"]:
            document = documents.get(source["id"])
            if document is None or not source["snippet"] or not document["content"].startswith(source["snippet"]):
                issues.append(f"{response['id']}: retrieved excerpt {source['id']} does not match the saved corpus.")
        if item["expected_behavior"] == "answer_with_citation":
            behavior = grounded_behavior(result.get("outputs.rubric.required_fact_recall"),
                                         result.get("outputs.rubric.forbidden_claim"), checks)
            if result.get("outputs.rubric.behavior_correctness") is not behavior:
                issues.append(f"{response['id']}: grounded behavior disagrees with facts/citation checks.")
    return issues


def compare_runs(folders: list[Path], output: Path) -> None:
    output.mkdir(parents=True, exist_ok=True)
    grouped: dict[str, list[dict[str, Any]]] = defaultdict(list)
    details: list[dict[str, Any]] = []
    validation: list[dict[str, Any]] = []
    signature: tuple[Any, ...] | None = None
    for folder in folders:
        manifest = read_json(folder / "manifest.json")
        scoring = read_json(folder / "scoring.json")
        current = (manifest["dataset_sha256"], manifest["repeats"], manifest["limit"], tuple(manifest.get("ids") or []),
                   scoring["judge"], scoring["sdk_version"], scoring.get("metric_adapter_version"))
        if signature is not None and signature != current:
            raise ValueError("Cannot compare runs with different datasets, workloads, judges or SDK versions.")
        signature = current
        run_checks: list[str] = []
        run_issues: list[str] = []
        local_dataset = manifest["dataset_sha256"] == file_hash(DATASET)
        items = {item["id"]: item for item in read_rows(DATASET)} if local_dataset else {}
        documents = {}
        if local_dataset:
            from .review import REVIEW, require_approved

            require_approved()
            run_checks.append("Approved dataset and review hashes verified.")
            selected = manifest.get("ids") or list(items)[:manifest["limit"]]
            expected_workload = {(id, repeat) for id in selected[:manifest["limit"]] for repeat in range(manifest["repeats"])}
            pinned = read_json(REVIEW)["live_corpus_sha256"]
            for name in ("corpus.before.json", "corpus.after.json"):
                path = folder / name
                if not path.exists():
                    run_issues.append(f"Missing saved corpus snapshot: {name}.")
                    continue
                snapshot = read_json(path)
                if corpus_hash(snapshot) != pinned:
                    run_issues.append(f"{name} does not match the reviewed corpus.")
                else:
                    run_checks.append(f"{name}: saved corpus matches the reviewed fingerprint.")
                if name == "corpus.before.json":
                    documents = {document["id"]: document for document in snapshot}
        else:
            run_checks.append("Dataset/source audit unavailable: run does not use the locally approved dataset.")
        for model in manifest["models"]:
            label = f"{folder.name}/{model}"
            results = read_json(folder / model / "scores.json")["rows"]
            responses = read_rows(folder / model / "responses.jsonl")
            expected = {(row["id"], row["repeat"]) for row in responses}
            actual = {(row.get("inputs.id"), row.get("inputs.repeat")) for row in results}
            if len(results) != len(responses) or len(expected) != len(responses) or actual != expected:
                raise ValueError(f"Missing or duplicated scored rows for {label}.")
            if local_dataset:
                if expected != expected_workload:
                    run_issues.append(f"{model}: response workload differs from the manifest.")
                model_issues = audit_model(responses, results, items, documents)
                run_issues.extend(f"{model}: {issue}" for issue in model_issues)
                if not model_issues:
                    run_checks.append(f"{model}: {len(results)} response/reference pairs, deterministic scores and source excerpts verified.")
            grouped[label].extend(results)
            by_key = {(row["id"], row["repeat"]): row for row in responses}
            for result in results:
                response = by_key[(result["inputs.id"], result["inputs.repeat"])]
                details.append({"model": label, "category": response["category"], "id": response["id"],
                                "repeat": response["repeat"], "query": response["query"], "response": response["response"],
                                "status": response["status"], "processing": response["processing"], "result": result,
                                "context": response["context"], "warnings": response["warnings"], "errors": response.get("errors", []),
                                "item": result.get("inputs.item", {}), "citations": response.get("citations", [])})
        validation.append({"run": folder.name, "status": "failed" if run_issues else "passed" if local_dataset else "not_verified",
                           "checks": run_checks, "issues": run_issues})
    metric_names = {name.split(".")[-1] for name in METRICS}
    keys = ["outputs." + name for name in METRICS if "." in name]
    summary: list[dict[str, Any]] = []
    for model, rows in grouped.items():
        categories = {"all": rows}
        for category in ("answerable", "unanswerable", "safety"):
            categories[category] = [row for row in rows if row.get("inputs.category") == category]
        categories["retrieval_hard"] = [row for row in rows if row.get("outputs.code.retrieval_hit_at_5") is False]
        for category, subset in categories.items():
            if not subset:
                continue
            for key in keys:
                summary.append({"model": model, "category": category, "metric": key.removeprefix("outputs."),
                                **metric_summary(subset, key, lower_is_better=key.endswith((".forbidden_claim", ".unexpected_tool_call")))})
            selected = {(row["inputs.id"], row["inputs.repeat"]) for row in subset}
            processing = [detail["processing"] for detail in details if detail["model"] == model
                          and (detail["id"], detail["repeat"]) in selected and detail["processing"]]
            for name, values in {
                "elapsed_ms": [p["elapsedMilliseconds"] for p in processing],
                "tokens_total": [p["tokens"]["total"] for p in processing if p.get("tokens")],
                "model_token_cost_usd": [p["estimatedModelCost"]["amount"] for p in processing if p.get("estimatedModelCost")],
            }.items():
                summary.append({"model": model, "category": category, "metric": name, "mean": statistics.mean(values) if values else None,
                                "std": statistics.pstdev(values) if values else None, "scored": len(values),
                                "pass_rate": None, "pass_denominator": 0, "total": len(subset), "eligible": len(subset),
                                "not_applicable": 0, "missing": 0,
                                "status": "scored" if len(values) == len(subset) else "partial" if values else "unavailable",
                                "reason": " ".join(sorted({p.get("costUnavailableReason") or "Cost metadata unavailable."
                                                          for p in processing if not p.get("estimatedModelCost")})) if name == "model_token_cost_usd"
                                          else "Incomplete usage/latency metadata." if len(values) != len(subset) else ""})
    fields = ["model", "category", "metric", "mean", "std", "scored", "pass_rate", "pass_denominator", "total",
              "eligible", "not_applicable", "missing", "status", "reason"]
    with (output / "comparison.csv").open("w", encoding="utf-8", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=fields)
        writer.writeheader()
        writer.writerows(summary)
    lines = ["# Agent evaluation comparison", "", "Local SDK scores; descriptive standard deviation, not a confidence interval.",
             "Missing scores/costs are not zero. Scored and pass denominators show coverage.",
             "Task adherence is binary: 1 means pass, 0 means failure (not a 1-5 score).",
             "Judge inference cost is separate from candidate token costs and is not estimated here.", "",
             "Empty subgroups are omitted. Coverage distinguishes eligible cases from all cases.",
             "| Run/model | Category | Metric | Mean +/- SD | Scored/eligible (total) | Pass rate (n) | Status |",
             "|---|---|---|---|---|---|---|"]
    for row in summary:
        value = formatted_mean(row)
        passed = f"{row['pass_rate']:.1%} ({row['pass_denominator']})" if row["pass_rate"] is not None else "N/A"
        lines.append(f"| {row['model']} | {row['category']} | {row['metric']} | {value} | {row['scored']}/{row['eligible']} ({row['total']}) | {passed} | {row['status']} |")
    (output / "comparison.md").write_text("\n".join(lines) + "\n", encoding="utf-8")
    template = (EVALS / "templates" / "report.html").read_text(encoding="utf-8")
    body: list[str] = []
    for row in summary:
        value = formatted_mean(row)
        passed = f"{row['pass_rate']:.1%} (n={row['pass_denominator']})" if row["pass_rate"] is not None else "No pass criterion"
        attrs = " ".join(f'data-{name}="{html.escape(str(row[name]), quote=True)}"' for name in ("model", "category", "metric", "status"))
        body.append(f"<tr {attrs}>" + "".join(f"<td>{html.escape(str(value))}</td>" for value in
                    (row["model"], row["category"], row["metric"], value,
                     f"{row['scored']}/{row['eligible']} eligible ({row['total']} total)" if row["eligible"]
                     else f"No eligible cases ({row['total']} total)", passed,
                     row["status"].replace("_", " "), row["reason"])) + "</tr>")
    drilldown: list[str] = []
    for detail in sorted(details, key=lambda detail: (
            detail["status"] == "ok", detail["result"].get("outputs.rubric.behavior_correctness") is True,
            detail["result"].get("outputs.groundedness.groundedness") is None,
            detail["result"].get("outputs.groundedness.groundedness") or 0)):
        text = html.escape
        scores = "<dl>" + "".join(f"<dt>{text(name.removeprefix('outputs.'))}</dt><dd>{text('N/A' if value is None else str(value))}</dd>"
                                  for name, value in detail["result"].items()
                                  if name.startswith("outputs.") and
                                  (name.split(".")[-1] in metric_names or name.endswith("_reason"))) + "</dl>"
        hard = "true" if detail["result"].get("outputs.code.retrieval_hit_at_5") is False else "false"
        drilldown.append(f'<details data-model="{text(detail["model"])}" data-category="{text(detail["category"])}" data-retrieval-hard="{hard}"><summary>{text(detail["model"])} | {text(detail["id"])} | '
                         f'{text(detail["category"])} | repeat {detail["repeat"]} | {text(detail["status"])}</summary>'
                         f'<h3>Question</h3><p>{text(detail["query"])}</p><h3>Answer</h3><p>{text(detail["response"])}</p>'
                         f'<h3>Expected answer and facts</h3><pre>{text(json.dumps(detail["item"].get("ground_truth"), ensure_ascii=False))}\n'
                         f'{text(json.dumps(detail["item"].get("required_facts", []), indent=2, ensure_ascii=False))}</pre>'
                         f'<h3>Server-owned citations</h3><pre>{text(json.dumps(detail["citations"], indent=2))}</pre>'
                         f'<h3>Scores and grading reasons</h3>{scores}'
                         f'<h3>Performance metadata</h3><pre>{text(json.dumps(detail["processing"], indent=2))}</pre>'
                         f'<h3>Retrieved context</h3><pre>{text(detail["context"])}</pre>'
                         f'<p>Warnings: {text(str(detail["warnings"]))}</p><p>Errors: {text(str(detail["errors"]))}</p></details>')
    workload = read_json(folders[0] / "manifest.json")
    description = f"{len(details)} responses across {len(grouped)} model runs."
    if workload.get("ids") or workload["limit"] < 50:
        description += f" Workload: {workload['repeats']} repeat(s) per question. " \
                       "This is a limited verification sample, not the full benchmark."
    write_json(output / "validation.json", {"runs": validation, "responses": len(details),
                                           "empty_subgroups_omitted": True})
    audit_html = []
    for run in validation:
        audit_html.append(f'<h3>{html.escape(run["run"])}: {html.escape(run["status"])}</h3><ul>' +
                          "".join(f"<li>{html.escape(check)}</li>" for check in run["checks"]) + "</ul>" +
                          "".join(f'<p class="error">{html.escape(issue)}</p>' for issue in run["issues"]))
    payload = json.dumps({"summary": summary, "models": list(grouped),
                          "metrics": {name: {"label": spec.label, "maximum": spec.maximum,
                                            "lowerIsBetter": spec.lower_is_better, "note": spec.note}
                                      for name, spec in METRICS.items()}}, allow_nan=False).replace("<", "\\u003c")
    (output / "comparison.html").write_text(template.replace("<!-- WORKLOAD -->", html.escape(description))
                                           .replace("<!-- SUMMARY -->", "\n".join(body))
                                           .replace("<!-- VALIDATION -->", "\n".join(audit_html))
                                           .replace("<!-- DATA -->", payload)
                                           .replace("<!-- DETAILS -->", "\n".join(drilldown)), encoding="utf-8")
    print(f"Comparison dashboard: {output / 'comparison.html'}")
    if any(run["issues"] for run in validation):
        raise ValueError("Saved evaluation validation failed; inspect validation.json and the dashboard's validation section.")
