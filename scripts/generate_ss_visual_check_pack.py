#!/usr/bin/env python3
"""Generate visual check pack (tasks + screenshot folders) for Cursor/human review."""

from __future__ import annotations

import argparse
import json
import shutil
from pathlib import Path
from typing import Any, Dict, List, Optional

ROOT = Path(__file__).resolve().parents[1]
ARTIFACTS = ROOT / "artifacts" / "streaming_survival" / "test_runs"
SCENARIOS = ROOT / "streaming_survival_tests" / "scenarios.json"
PREVIEW = ROOT / ".train_lab_ui" / "ss_preview" / "frame.jpg"


def _latest_run() -> Optional[Path]:
    latest = ARTIFACTS / "LATEST"
    if latest.is_file():
        p = Path(latest.read_text(encoding="utf-8").strip())
        if p.is_dir():
            return p
    if not ARTIFACTS.is_dir():
        return None
    runs = sorted([d for d in ARTIFACTS.iterdir() if d.is_dir()], reverse=True)
    return runs[0] if runs else None


def generate(run_dir: Path) -> Path:
    scenarios = json.loads(SCENARIOS.read_text(encoding="utf-8"))
    tasks: List[Dict[str, Any]] = []
    shot_root = run_dir / "screenshots"
    shot_root.mkdir(parents=True, exist_ok=True)

    for sc in scenarios:
        if not sc.get("require_visual_check") and sc.get("id") not in (
            "go_to_water_only",
            "go_home_only",
            "go_to_campfire_only",
            "wrong_target_visual_water",
            "seq_campfire_wood_water",
        ):
            continue
        sid = sc["id"]
        d = shot_root / sid
        d.mkdir(parents=True, exist_ok=True)
        # Seed with latest SS preview frame if present (annotated_final for Cursor review).
        final = d / "annotated_final.png"
        if PREVIEW.is_file():
            shutil.copy2(PREVIEW, d / "002_final.png")
            shutil.copy2(PREVIEW, final)
        else:
            (d / "README.txt").write_text(
                "Place 000_start.png / 001_mid.png / 002_final.png / annotated_final.png here.\n",
                encoding="utf-8",
            )
        q = sc.get("visual_question") or (
            f"Does the character match expected action/target for {sid}? "
            f"plan={sc.get('expected_plan')}"
        )
        tasks.append(
            {
                "scenario_id": sid,
                "image": f"screenshots/{sid}/annotated_final.png",
                "question": q,
                "expected": "yes",
                "expected_plan": sc.get("expected_plan"),
                "expected_target_type": sc.get("expected_target_type"),
            }
        )

    tasks_path = run_dir / "visual_check_tasks.json"
    tasks_path.write_text(json.dumps(tasks, ensure_ascii=False, indent=2), encoding="utf-8")
    # Do not invent PASS — leave verdicts for Cursor/human.
    verdicts = run_dir / "visual_verdicts.json"
    if not verdicts.is_file():
        stub = {
            t["scenario_id"]: {
                "verdict": "PENDING",
                "reason": "Awaiting Cursor/human visual review.",
            }
            for t in tasks
        }
        verdicts.write_text(json.dumps(stub, ensure_ascii=False, indent=2), encoding="utf-8")
    return tasks_path


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--run-dir", type=str, default="")
    args = ap.parse_args()
    run = Path(args.run_dir) if args.run_dir else _latest_run()
    if run is None:
        print("FAIL: no run dir")
        return 1
    path = generate(run)
    print(path)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
