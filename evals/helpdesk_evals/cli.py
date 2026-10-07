from __future__ import annotations

import argparse
from datetime import datetime, timezone
from pathlib import Path
import subprocess

from .common import CONFIG, DATASET, EVALS, ROOT, azure_environment, read_json, runner
from .dataset import build_dataset, validate_offline


def main() -> None:
    parser = argparse.ArgumentParser(description="Local Azure AI SDK evaluations of the actual helpdesk agent.")
    commands = parser.add_subparsers(dest="command", required=True)
    commands.add_parser("build-dataset")
    validate = commands.add_parser("validate")
    validate.add_argument("--live", action="store_true")
    validate.add_argument("--review", action="store_true", help="Run two independent model reviews, requires --live.")
    approve = commands.add_parser("approve")
    approve.add_argument("--reviewer", required=True, help="Name of the human who inspected the review report.")
    run = commands.add_parser("run")
    run.add_argument("--models", nargs="+")
    run.add_argument("--repeats", type=int, default=3)
    run.add_argument("--limit", type=int, default=50)
    run.add_argument("--ids", nargs="+", help="Explicit item IDs for a balanced smoke run.")
    run.add_argument("--output", type=Path)
    score = commands.add_parser("score")
    score.add_argument("run", type=Path)
    score.add_argument("--metrics", nargs="+", choices=[
        "groundedness", "relevance", "completeness", "similarity", "f1", "retrieval", "intent", "task", "tools", "code", "rubric",
    ], help="Re-score only selected evaluators, preserving other existing scores.")
    compare = commands.add_parser("compare")
    compare.add_argument("runs", nargs="+", type=Path)
    compare.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()

    if args.command == "build-dataset":
        subprocess.run(["dotnet", "build", str(EVALS / "runner" / "Helpdesk.Evaluation.Runner.csproj"), "--nologo"],
                       cwd=ROOT, check=True)
        runner("seed", EVALS / "datasets" / "seed-documents.json")
        build_dataset(EVALS / "datasets" / "seed-documents.json")
    if args.command in ("build-dataset", "validate"):
        failures = validate_offline()
        if failures:
            raise RuntimeError("\n".join(failures))
        print("Offline gates passed: 50 items, exact supporting spans, complete equivalent sources and no near duplicates.")
        if args.command == "validate" and args.review and not args.live:
            parser.error("--review requires --live")
        if args.command == "validate" and args.live:
            from .review import validate_live
            validate_live(azure_environment(), review=args.review)
    elif args.command == "approve":
        from .review import approve_dataset
        approve_dataset(args.reviewer)
    elif args.command == "run":
        from .review import require_approved, verify_live
        require_approved()
        failures = validate_offline()
        if failures:
            raise RuntimeError("\n".join(failures))
        environment = azure_environment()
        output = args.output or EVALS / "results" / datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S%fZ")
        output.mkdir(parents=True, exist_ok=False)
        verify_live(environment, output / "corpus.before.json")
        models = args.models or [model["Id"] for model in read_json(CONFIG)["AzureOpenAI"]["Models"]]
        if len(models) != len(set(models)):
            parser.error("Duplicate model selections are not allowed.")
        for model in models:
            if model not in {entry["Id"] for entry in read_json(CONFIG)["AzureOpenAI"]["Models"]}:
                parser.error(f"Unknown candidate {model}")
        from .common import file_hash, write_json
        write_json(output / "manifest.json", {
            "models": models, "repeats": args.repeats, "limit": args.limit,
            "ids": args.ids,
            "dataset_sha256": file_hash(DATASET), "config": read_json(CONFIG),
            "created_at": datetime.now(timezone.utc).isoformat(),
        })
        failures = []
        for model in models:
            folder = output / model
            folder.mkdir()
            options = {"dataset": str(DATASET), "model": model, "repeats": str(args.repeats), "limit": str(args.limit)}
            if args.ids:
                options["ids"] = ",".join(args.ids)
            try:
                runner("run", folder / "responses.jsonl", environment, **options)
            except subprocess.CalledProcessError as error:
                from .common import read_rows
                response_file = folder / "responses.jsonl"
                if not response_file.exists():
                    raise
                rows = read_rows(response_file)
                expected = min(args.limit, len(args.ids) if args.ids else 50) * args.repeats
                if len(rows) != expected or not any(row["status"] == "error" for row in rows):
                    raise
                failures.append(f"{model}: runner returned {error.returncode}; operational failures persisted.")
        verify_live(environment, output / "corpus.after.json")
        print(f"Responses saved to {output}. Run helpdesk-eval score \"{output}\".")
        if failures:
            raise RuntimeError("\n".join(failures))
    elif args.command == "score":
        from .scoring import score_run
        score_run(args.run, azure_environment(), args.metrics)
    elif args.command == "compare":
        from .report import compare_runs
        compare_runs(args.runs, args.output)


if __name__ == "__main__":
    main()
