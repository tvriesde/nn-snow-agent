from __future__ import annotations

import hashlib
import json
import math
import os
from pathlib import Path
import shutil
import subprocess
from typing import Any

ROOT = Path(__file__).resolve().parents[2]
EVALS = ROOT / "evals"
DATASET = EVALS / "datasets" / "ground_truth.jsonl"
CONFIG = EVALS / "config.json"


def read_json(path: Path) -> Any:
    return json.loads(path.read_text(encoding="utf-8-sig"))


def write_json(path: Path, value: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(json_safe(value), indent=2, ensure_ascii=True, allow_nan=False) + "\n", encoding="utf-8")


def json_safe(value: Any) -> Any:
    if isinstance(value, float) and not math.isfinite(value):
        return None
    if isinstance(value, dict):
        return {key: json_safe(item) for key, item in value.items()}
    if isinstance(value, (list, tuple)):
        return [json_safe(item) for item in value]
    return value


def read_rows(path: Path) -> list[dict[str, Any]]:
    return [json.loads(line) for line in path.read_text(encoding="utf-8-sig").splitlines() if line.strip()]


def file_hash(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def corpus_hash(documents: list[dict[str, Any]]) -> str:
    # Runtime evidence fields only; ingestion version/timestamps are not answer content.
    fields = ("id", "number", "title", "content", "application", "visibility")
    canonical = [{key: doc[key] for key in fields} for doc in sorted(documents, key=lambda doc: doc["id"])]
    return hashlib.sha256(json.dumps(canonical, sort_keys=True, ensure_ascii=True).encode()).hexdigest()


def az_json(*args: str) -> Any:
    executable = shutil.which("az")
    if executable is None:
        raise RuntimeError("Azure CLI is missing. Install it, then run az login.")
    result = subprocess.run([executable, *args, "--only-show-errors", "-o", "json"], capture_output=True, text=True)
    if result.returncode:
        raise RuntimeError(f"Azure CLI {args[0]} failed: {result.stderr.strip()}")
    return json.loads(result.stdout)


def azure_environment() -> dict[str, str]:
    ownership = read_json(ROOT / ".azure" / "ownership.json")
    account = az_json("account", "show")
    if account["id"] != ownership["subscriptionId"] or account["tenantId"] != ownership["tenantId"]:
        raise RuntimeError("Active Azure CLI subscription/tenant differs from .azure ownership. Run az account set.")
    settings = az_json("webapp", "config", "appsettings", "list", "--subscription", ownership["subscriptionId"],
                      "-g", ownership["resourceGroupName"], "-n", ownership["resources"]["backendName"])
    # Model catalog comes from config.json; preserve only verified existing prices from the app.
    result = os.environ.copy()
    prices: dict[str, dict[str, str]] = {}
    ids = {setting["name"].split("__")[2]: setting["value"] for setting in settings
           if setting["name"].startswith("AzureOpenAI__Models__") and setting["name"].endswith("__Id")}
    for setting in settings:
        name, value = setting["name"], setting["value"]
        if name.startswith(("Search__", "Azure__")) or name in ("AzureOpenAI__Endpoint", "AzureOpenAI__ApiKey"):
            result[name] = value
        elif name.startswith("AzureOpenAI__Models__") and "__Pricing__" in name:
            parts = name.split("__")
            prices.setdefault(ids[parts[2]], {})[parts[-1]] = value
    for index, model in enumerate(read_json(CONFIG)["AzureOpenAI"]["Models"]):
        for key, value in prices.get(model["Id"], {}).items():
            result[f"AzureOpenAI__Models__{index}__Pricing__{key}"] = value
    reference = result.get("AzureOpenAI__ApiKey", "")
    if not reference.startswith("@Microsoft.KeyVault("):
        raise RuntimeError("Expected a Key Vault secret reference, not a plaintext credential.")
    fields = dict(part.split("=", 1) for part in reference.removeprefix("@Microsoft.KeyVault(").removesuffix(")").split(";"))
    secret = az_json("keyvault", "secret", "show", "--vault-name", fields["VaultName"], "--name", fields["SecretName"])
    result["AzureOpenAI__ApiKey"] = secret["value"]
    return result


def runner(command: str, output: Path, environment: dict[str, str] | None = None, **options: str) -> None:
    args = ["dotnet", "run", "--no-build", "--project", str(EVALS / "runner" / "Helpdesk.Evaluation.Runner.csproj"),
            "--", command, "--output", str(output), "--config", str(CONFIG)]
    if command == "seed":
        args.extend(["--root", str(ROOT)])
    for name, value in options.items():
        args.extend([f"--{name.replace('_', '-')}", value])
    subprocess.run(args, cwd=ROOT, env=environment, check=True)
