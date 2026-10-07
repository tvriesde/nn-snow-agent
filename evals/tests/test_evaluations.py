import copy
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import Mock

from helpdesk_evals.common import DATASET, EVALS, corpus_hash, json_safe, read_json, read_rows
from helpdesk_evals.dataset import validate_dataset, validate_offline
from helpdesk_evals.report import audit_model, compare_runs, formatted_mean, metric_summary, sdk_input_equal
from helpdesk_evals.scoring import SdkMetric, agent_messages, code_metrics, grounded_behavior, normalize_applicability, normalize_grounded_behavior


class DatasetTests(unittest.TestCase):
    def setUp(self):
        self.items = read_rows(DATASET)
        self.documents = read_json(EVALS / "datasets" / "seed-documents.json")

    def test_all_50_references_are_supported(self):
        self.assertEqual(validate_offline(), [])

    def test_extra_reference_claim_fails(self):
        self.items[0]["ground_truth"] += " The password expires every 90 days."
        self.assertTrue(any("claims outside" in message for message in validate_dataset(self.items, self.documents)))

    def test_missing_equivalent_source_fails(self):
        self.items[0]["expected_source_ids"].pop()
        self.assertTrue(any("incomplete equivalent" in message for message in validate_dataset(self.items, self.documents)))

    def test_live_evidence_mutation_fails(self):
        self.documents[0]["content"] = "Updated unsupported content"
        self.assertTrue(validate_dataset(self.items, self.documents))

    def test_unresolved_incidents_not_projected(self):
        source = read_json(EVALS.parent / "data" / "servicenow" / "baseline" / "incident.json")
        unresolved = {row["sys_id"] for row in source["result"] if row["incident_state"] not in ("6", "7")}
        self.assertFalse(unresolved & {row["sourceId"] for row in self.documents})

    def test_corpus_hash_ignores_ingestion_metadata_but_not_evidence(self):
        before = corpus_hash(self.documents)
        self.documents[0]["sourceVersion"] += 1
        self.assertEqual(before, corpus_hash(self.documents))
        self.documents[0]["content"] += " New fact."
        self.assertNotEqual(before, corpus_hash(self.documents))

    def test_no_answerable_items_reuse_same_fact_set(self):
        sets = [tuple(item["required_facts"]) for item in self.items if item["category"] == "answerable"]
        self.assertEqual(len(sets), len(set(sets)))


class MetricTests(unittest.TestCase):
    def setUp(self):
        self.row = {"status": "ok", "retrieved_sources": [{"id": "kb1"}], "citations": ["kb1"],
                    "raw_answer": '{"answer":"x","knowledgeSourceIds":["kb1"]}',
                    "retrievals": [{"sources": [{"id": "kb1"}]}], "tool_calls": []}
        self.item = {"expected_source_ids": ["kb1", "inc1"]}

    def test_correct_equivalent_citation(self):
        result = code_metrics(self.row, self.item)
        self.assertTrue(result["citation_validity"])
        self.assertTrue(result["citation_correctness"])
        self.assertTrue(result["retrieval_hit_at_5"])

    def test_grounded_behavior_uses_source_metadata_not_inline_citations(self):
        self.assertTrue(grounded_behavior(1, False, code_metrics(self.row, self.item)))

    def test_grounded_behavior_rejects_incomplete_facts_and_forbidden_claims(self):
        checks = code_metrics(self.row, self.item)
        self.assertFalse(grounded_behavior(.5, False, checks))
        self.assertFalse(grounded_behavior(1, True, checks))
        self.row["citations"] = []
        self.assertFalse(grounded_behavior(1, False, code_metrics(self.row, self.item)))

    def test_citation_gate_corrects_grader_format_confusion_only_for_answerable_cases(self):
        result = {"rows": [{"inputs.id": "GT-001", "inputs.row": self.row, "outputs.rubric.required_fact_recall": 1,
                           "outputs.rubric.forbidden_claim": False, "outputs.rubric.behavior_correctness": False,
                           },
                          {"inputs.id": "GT-039", "outputs.rubric.behavior_correctness": False}]}
        normalize_grounded_behavior(result, {"GT-001": dict(self.item, expected_behavior="answer_with_citation"),
                                            "GT-039": {"expected_behavior": "state_no_knowledge"}})
        self.assertTrue(result["rows"][0]["outputs.rubric.behavior_correctness"])
        self.assertFalse(result["rows"][1]["outputs.rubric.behavior_correctness"])

    def test_raw_hallucinated_id_not_masked_by_ledger(self):
        self.row["raw_answer"] = '{"answer":"x","knowledgeSourceIds":["invented"]}'
        self.row["citations"] = []
        self.assertFalse(code_metrics(self.row, self.item)["citation_validity"])

    def test_no_context_cannot_receive_groundedness_pass(self):
        sdk = Mock()
        evaluator = SdkMetric("groundedness", sdk, grounded_only=True)
        result = evaluator("q", "a", "", "gt", "answerable", "ok", [], [])
        self.assertEqual(result["groundedness_result"], "fail")
        sdk.assert_not_called()

    def test_safety_grounding_is_not_treated_as_retrieval_failure(self):
        sdk = Mock()
        result = SdkMetric("groundedness", sdk, grounded_only=True)("q", "a", "", "gt", "safety", "ok", [], [])
        self.assertFalse(result["groundedness_applicable"])
        sdk.assert_not_called()

    def test_failed_rows_are_failures_not_synthetic_answers(self):
        sdk = Mock()
        result = SdkMetric("relevance", sdk)("q", "fallback", "context", "gt", "answerable", "error", [], [])
        self.assertEqual(result["relevance_result"], "fail")
        sdk.assert_not_called()

    def test_unavailable_scores_not_zero_and_failures_in_denominator(self):
        summary = metric_summary([{"m": 5, "m_result": "pass"}, {"m_result": "fail"}, {}], "m")
        self.assertEqual(summary["mean"], 5)
        self.assertEqual(summary["scored"], 1)
        self.assertEqual(summary["pass_rate"], .5)

    def test_false_safety_violation_flag_is_a_pass(self):
        summary = metric_summary([{"flag": False}, {"flag": False}, {"flag": True}], "flag", lower_is_better=True)
        self.assertAlmostEqual(summary["pass_rate"], 2 / 3)
        self.assertEqual(summary["scored"], 3)
        self.assertAlmostEqual(summary["mean"], 1 / 3)

    def test_relevance_and_intent_skip_refusal_and_abstention_without_judge_calls(self):
        for category in ("unanswerable", "safety"):
            for metric in ("relevance", "intent_resolution"):
                sdk = Mock()
                result = SdkMetric(metric, sdk)("q", "safe answer", "c", "gt", category, "ok", [], [])
                self.assertFalse(result[metric + "_applicable"])
                sdk.assert_not_called()

    def test_legacy_inappropriate_judgments_are_preserved_but_excluded(self):
        result = {"rows": [{"inputs.id": "GT-042", "inputs.repeat": 0, "inputs.category": "unanswerable",
                           "outputs.relevance.relevance": 2, "outputs.relevance.relevance_result": "fail",
                           "outputs.relevance.relevance_reason": "Did not invent clock times."}]}
        normalize_applicability(result)
        normalize_applicability(result)
        self.assertEqual(len(result["excluded_scores"]), 1)
        self.assertEqual(result["excluded_scores"][0]["score"], 2)
        self.assertIsNone(result["rows"][0]["outputs.relevance.relevance"])
        self.assertNotIn("outputs.relevance.relevance_result", result["rows"][0])

    def test_coverage_distinguishes_inapplicable_from_missing_scores(self):
        result = metric_summary([
            {"inputs.category": "answerable", "outputs.groundedness.groundedness": 5},
            {"inputs.category": "unanswerable", "outputs.groundedness.groundedness": None},
        ], "outputs.groundedness.groundedness")
        self.assertEqual((result["eligible"], result["scored"], result["not_applicable"]), (1, 1, 1))
        self.assertEqual(result["status"], "partial")
        self.assertEqual(result["missing"], 0)
        missing = metric_summary([{"inputs.category": "answerable"}], "outputs.groundedness.groundedness")
        self.assertEqual(missing["status"], "missing")
        self.assertEqual(missing["missing"], 1)

    def test_f1_pass_denominator_uses_actual_sdk_field(self):
        result = metric_summary([{"outputs.f1.f1_score": .2, "outputs.f1.f1_result": "fail"},
                                 {"outputs.f1.f1_score": .6, "outputs.f1.f1_result": "pass"}], "outputs.f1.f1_score")
        self.assertEqual(result["pass_rate"], .5)
        self.assertEqual(result["pass_denominator"], 2)

    def test_saved_audit_detects_tampered_excerpt_and_inconsistent_score(self):
        response = {**self.row, "id": "GT-001", "repeat": 0,
                    "retrieved_sources": [{"id": "kb1", "snippet": "tampered"}]}
        item = dict(self.item, expected_behavior="answer_with_citation")
        result = {"inputs.id": "GT-001", "inputs.repeat": 0, "inputs.row": response, "inputs.item": item,
                  **{"outputs.code." + key: value for key, value in code_metrics(response, item).items()},
                  "outputs.rubric.required_fact_recall": 1, "outputs.rubric.forbidden_claim": False,
                  "outputs.rubric.behavior_correctness": True}
        result["outputs.code.citation_validity"] = False
        issues = audit_model([response], [result], {"GT-001": item}, {"kb1": {"content": "real content"}})
        self.assertTrue(any("excerpt" in issue for issue in issues))
        self.assertTrue(any("citation_validity" in issue for issue in issues))

    def test_sdk_cost_roundtrip_tolerance_does_not_hide_real_input_changes(self):
        self.assertTrue(sdk_input_equal({"cost": .000457985}, {"cost": .00045798500000000005}))
        self.assertFalse(sdk_input_equal({"cost": .000457985}, {"cost": .000457986}))
        self.assertFalse(sdk_input_equal({"answer": "safe"}, {"answer": "different"}))
        self.assertFalse(sdk_input_equal(True, 1))
        self.assertFalse(sdk_input_equal(1000000000000, 1000000000001))
        self.assertIn("0.0004580", formatted_mean({"metric": "model_token_cost_usd",
                                                  "mean": .000457985, "std": 0, "status": "scored"}))

    def test_task_adherence_receives_actual_system_instructions(self):
        sdk = Mock(return_value={"task_adherence": 0})
        SdkMetric("task_adherence", sdk)("q", "a", "c", "gt", "answerable", "ok",
                                      [{"instructions": "real instructions", "tool_definitions": []}], [])
        self.assertEqual(sdk.call_args.kwargs["query"][0]["content"], "real instructions")

    def test_task_grades_model_json_not_user_facing_plain_text(self):
        sdk = Mock(return_value={"task_adherence": 1})
        raw = '{"answer":"actual answer","knowledgeSourceIds":[]}'
        rounds = [{"instructions": "Return JSON", "input": [], "tool_definitions": [],
                   "output": [{"role": "assistant", "content": [{"type": "text", "text": raw}]}]}]
        SdkMetric("task_adherence", sdk)("q", "actual answer", "c", "gt", "answerable", "ok", rounds, [])
        self.assertEqual(sdk.call_args.kwargs["response"][0]["content"], raw)

    def test_task_receives_evidence_even_when_sdk_keeps_only_last_user_message(self):
        sdk = Mock(return_value={"task_adherence": 1})
        rounds = [{"instructions": "Only cite retrieved IDs", "tool_definitions": [], "input": [
            {"role": "user", "content": [{"type": "text", "text": "Current evidence: real-id"}]},
            {"role": "user", "content": [{"type": "text", "text": "Question"}]},
        ]}]
        SdkMetric("task_adherence", sdk)("Question", "a", "c", "gt", "answerable", "ok", rounds, [])
        last_user = sdk.call_args.kwargs["query"][-1]["content"]
        self.assertIn("real-id", last_user)
        self.assertIn("Question", last_user)

    def test_sdk_not_applicable_nan_becomes_strict_json_null(self):
        self.assertEqual(json_safe({"score": float("nan"), "other": [float("inf"), 5.0]}),
                         {"score": None, "other": [None, 5.0]})


class ReportTests(unittest.TestCase):
    def test_dashboard_is_generated_and_untrusted_answers_are_escaped(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            folder = root / "run"
            model = folder / "test-model"
            model.mkdir(parents=True)
            (folder / "manifest.json").write_text(json.dumps({
                "models": ["test-model"], "dataset_sha256": "test-hash", "repeats": 1, "limit": 2, "ids": ["GT-001", "GT-040"],
            }))
            (folder / "scoring.json").write_text(json.dumps({"judge": "judge", "sdk_version": "1.15.0"}))
            response = {"id": "GT-001", "repeat": 0, "category": "answerable", "query": "<script>question</script>",
                        "response": "<img src=x onerror=alert(1)>", "context": "evidence", "status": "ok",
                        "processing": None, "warnings": []}
            (model / "responses.jsonl").write_text(json.dumps(response) + "\n" + json.dumps({
                **response, "id": "GT-040", "category": "unanswerable", "query": "Missing detail", "response": "Unknown.",
            }) + "\n")
            (model / "scores.json").write_text(json.dumps({"rows": [{
                "inputs.id": "GT-001", "inputs.repeat": 0, "inputs.category": "answerable",
                "outputs.groundedness.groundedness": 5, "outputs.groundedness.groundedness_result": "pass",
            }, {"inputs.id": "GT-040", "inputs.repeat": 0, "inputs.category": "unanswerable",
                "outputs.groundedness.groundedness": None}]}))
            compare_runs([folder], root / "report")
            page = (root / "report" / "comparison.html").read_text(encoding="utf-8")
            self.assertIn("&lt;script&gt;question&lt;/script&gt;", page)
            self.assertNotIn("<img src=x", page)
            self.assertIn("2 responses", page)
            self.assertIn("limited verification", page)
            self.assertNotIn("<!-- SUMMARY -->", page)
            self.assertTrue((root / "report" / "comparison.csv").exists())
            self.assertTrue((root / "report" / "comparison.md").exists())
            self.assertTrue((root / "report" / "validation.json").exists())
            payload = json.loads(page.split('<script type="application/json" id="report-data">')[1].split("</script>")[0])
            self.assertFalse(any(row["total"] == 0 for row in payload["summary"]))
            self.assertFalse(any(row["category"] == "retrieval_hard" for row in payload["summary"]))
            self.assertIn("tools.tool_call_accuracy", payload["metrics"])
            self.assertTrue(any(row["metric"] == "tools.tool_call_accuracy" and row["status"] == "not_applicable"
                                for row in payload["summary"]))
            self.assertIn('id="model-filter"', page)
            self.assertIn('id="category-filter"', page)
            self.assertIn('id="metric-filter"', page)
            self.assertIn('id="status-filter"', page)
            self.assertIn('id="charts"', page)
            self.assertNotIn("<!-- DATA -->", page)
            self.assertTrue(any(row["category"] == "unanswerable" and row["metric"] == "elapsed_ms"
                                for row in payload["summary"]))

    def test_empty_subgroup_is_skipped_per_model_not_for_entire_cohort(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            folder = root / "run"
            folder.mkdir()
            (folder / "manifest.json").write_text(json.dumps({
                "models": ["hit", "miss"], "dataset_sha256": "test-hash", "repeats": 1, "limit": 1, "ids": ["GT-001"]}))
            (folder / "scoring.json").write_text(json.dumps({"judge": "judge", "sdk_version": "1.15.0"}))
            response = {"id": "GT-001", "repeat": 0, "category": "answerable", "query": "q", "response": "a",
                        "context": "c", "status": "ok", "processing": None, "warnings": []}
            for model, hit in (("hit", True), ("miss", False)):
                directory = folder / model
                directory.mkdir()
                (directory / "responses.jsonl").write_text(json.dumps(response) + "\n")
                (directory / "scores.json").write_text(json.dumps({"rows": [{
                    "inputs.id": "GT-001", "inputs.repeat": 0, "inputs.category": "answerable",
                    "inputs.item": {"expected_source_ids": ["expected"]},
                    "outputs.code.retrieval_hit_at_5": hit}]}))
            compare_runs([folder], root / "report")
            page = (root / "report" / "comparison.html").read_text(encoding="utf-8")
            payload = json.loads(page.split('<script type="application/json" id="report-data">')[1].split("</script>")[0])
            hard = [row for row in payload["summary"] if row["category"] == "retrieval_hard"]
            self.assertTrue(hard)
            self.assertEqual({row["model"] for row in hard}, {"run/miss"})
            self.assertEqual(payload["models"], ["run/hit", "run/miss"])


if __name__ == "__main__":
    unittest.main()
