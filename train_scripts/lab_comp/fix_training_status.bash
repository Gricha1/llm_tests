#!/usr/bin/env bash
# Починка битого training_status.json для --resume.
# ml-agents 1.1 читает results/<RUN_ID>/run_logs/training_status.json (не только корневой).
#
#   bash train_scripts/lab_comp/fix_training_status.bash --resume
#   bash train_scripts/lab_comp/fix_training_status.bash jack_stream_05_07_2026_copy

set -eu
set -o pipefail

resolve_project_root() {
  local dir
  dir="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")" && pwd)"
  while [ "${dir}" != "/" ]; do
    if [ -f "${dir}/train_scripts/promote_latest_checkpoint.bash" ]; then
      echo "${dir}"
      return 0
    fi
    dir="$(dirname "${dir}")"
  done
  echo "ERROR: корень forest_survival не найден" >&2
  exit 1
}

ROOT="$(resolve_project_root)"
cd "${ROOT}"

RUN_ID="${RUN_ID:-jack_stream_05_07_2026_copy}"
DO_RESUME=0

for arg in "$@"; do
  case "${arg}" in
    --resume) DO_RESUME=1 ;;
    -h|--help)
      sed -n '1,10p' "$0"
      exit 0
      ;;
    *)
      if [ "${arg}" != "--resume" ]; then
        RUN_ID="${arg}"
      fi
      ;;
  esac
done

RUN_DIR="${ROOT}/results/${RUN_ID}"
RUN_DIR_REL="results/${RUN_ID}"

if [ ! -d "${RUN_DIR}" ]; then
  echo "ERROR: каталог run не найден: ${RUN_DIR}" >&2
  exit 1
fi

mkdir -p "${RUN_DIR}/run_logs"

python3 - "${RUN_DIR}" <<'PY'
import json, os, re, sys, time

run_dir = sys.argv[1]
targets = [
    os.path.join(run_dir, "training_status.json"),
    os.path.join(run_dir, "run_logs", "training_status.json"),
]

# Любые другие training_status.json внутри run (на всякий случай)
for root, _, files in os.walk(run_dir):
    for name in files:
        if name == "training_status.json":
            p = os.path.join(root, name)
            if p not in targets:
                targets.append(p)

meta = {"mlagents_version": "1.1.0", "torch_version": "2.0.1+cu117"}
for path in targets:
    if os.path.isfile(path):
        try:
            with open(path, "r", encoding="utf-8") as f:
                json.load(f)
            print(f"[fix] OK (уже валидный): {path}")
            continue
        except Exception:
            pass
        bak = f"{path}.bak.{time.strftime('%Y%m%d_%H%M%S')}"
        try:
            os.replace(path, bak)
            print(f"[fix] backup: {bak}")
        except OSError:
            pass
        text = open(bak, "r", encoding="utf-8", errors="replace").read() if os.path.isfile(bak) else ""
        m = re.search(r'"metadata"\s*:\s*(\{.*?\})\s*,\s*"name"', text, re.S)
        if m:
            try:
                meta = json.loads(m.group(1))
            except Exception:
                pass
    else:
        print(f"[fix] создаём: {path}")

    behaviors = {}
    for name in sorted(os.listdir(run_dir)):
        sub = os.path.join(run_dir, name)
        if not os.path.isdir(sub) or name in ("videos", "run_logs"):
            continue
        if os.path.isfile(os.path.join(sub, "checkpoint.pt")):
            behaviors[name] = {"checkpoints": [], "final_checkpoint": {}, "elo": 0.0}

    if not behaviors:
        print("ERROR: нет checkpoint.pt в behavior-папках", run_dir, file=sys.stderr)
        sys.exit(1)

    out = {"metadata": meta, "name": os.path.basename(run_dir), "behaviors": behaviors}
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8") as f:
        json.dump(out, f, indent=2)
        f.write("\n")
    json.load(open(path, "r", encoding="utf-8"))
    print(f"[fix] записан: {path} ({len(behaviors)} behavior)")
PY

if [ "${DO_RESUME}" -eq 1 ]; then
  bash train_scripts/promote_latest_checkpoint.bash "${RUN_DIR_REL}"
  exec bash train_scripts/lab_comp/train_stream.bash --resume
fi

echo "[fix] готово. Запуск: bash train_scripts/lab_comp/train_stream.bash --resume"
