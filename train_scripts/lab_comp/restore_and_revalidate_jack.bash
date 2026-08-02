#!/usr/bin/env bash
# Restore Jack checkpoint from best numbered .pt, re-validate all Jack tasks.
set -eu
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")/../.." && pwd)"
cd "${ROOT}"
BUILD="${1:-stream_forest_survival_2_12_07_2026}"
SRC="results/97_stage2/JackLowLevelAgent"
BEST="$(ls -1 "${SRC}"/JackLowLevelAgent-*.pt 2>/dev/null | sort -t- -k2 -n | tail -1)"
[ -n "${BEST}" ] || { echo "ERROR: no numbered pt"; exit 1; }
echo "[restore] ${BEST} → checkpoint.pt"
cp -f "${BEST}" "${SRC}/checkpoint.pt"
md5sum "${BEST}" "${SRC}/checkpoint.pt"
# Rebuild minimal training_status so future train --resume works.
python3 - <<PY
import json, re
from pathlib import Path
pt = Path("${BEST}")
step = int(re.search(r"-(\d+)\\.pt$", pt.name).group(1))
run = Path("results/97_stage2")
status = {
    "metadata": {
        "stats_format_version": "0.3.0",
        "mlagents_version": "1.1.0",
        "torch_version": "2.0.1+cu117",
    },
    "JackLowLevelAgent": {
        "step": step,
        "checkpoints": [{
            "steps": step,
            "file_path": f"results/97_stage2/JackLowLevelAgent/{pt.name}",
            "reward": None,
            "creation_time": 0.0,
            "auxillary_file_paths": [],
        }],
        "final_checkpoint": {
            "steps": step,
            "file_path": "results/97_stage2/JackLowLevelAgent/checkpoint.pt",
            "reward": None,
            "creation_time": 0.0,
            "auxillary_file_paths": [],
        },
    },
}
(run / "run_logs").mkdir(parents=True, exist_ok=True)
(run / "run_logs" / "training_status.json").write_text(json.dumps(status, indent=4), encoding="utf-8")
print("status step", step)
PY

for task in wood food water zombie; do
  echo "=== jack ${task} ==="
  bash train_scripts/lab_comp/validate_ui_one.bash "${BUILD}" 97_stage2 "${task}" 20 "" jack
done
echo "=== done ==="
ls -lh results/97_stage2/videos/ui_val_jack_*.mp4
# Confirm source run checkpoint still matches best
python3 - <<PY
import hashlib
from pathlib import Path
src = Path("results/97_stage2/JackLowLevelAgent")
best = sorted(src.glob("JackLowLevelAgent-*.pt"), key=lambda p: int(p.stem.split("-")[-1]))[-1]
ck = src / "checkpoint.pt"
print("post_check same", hashlib.md5(best.read_bytes()).digest() == hashlib.md5(ck.read_bytes()).digest(), best.name)
PY
