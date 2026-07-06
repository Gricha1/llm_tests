#!/usr/bin/env bash
# Копирует последний по шагу файл <Behavior>-<steps>.pt в checkpoint.pt для resume mlagents-learn.
#
# Usage (из любой директории, путь к run — как папка в results):
#   bash train_scripts/promote_latest_checkpoint.bash <run_folder>
#
# Примеры:
#   bash train_scripts/promote_latest_checkpoint.bash run_40_curic
#   bash train_scripts/promote_latest_checkpoint.bash results/run_40_curic
#
# Пропускает: results/<run>/videos/

set -eu
set -o pipefail

if [ -z "${1:-}" ]; then
  echo "Usage: bash train_scripts/promote_latest_checkpoint.bash <run_folder>"
  echo "  run_folder: например run_40_curic или results/run_40_curic"
  exit 1
fi

cd "$(dirname "$0")"
cd ..

PY_BIN="${PY_BIN:-}"
if [ -z "${PY_BIN}" ] && [ -n "${CONDA_PREFIX:-}" ] && [ -x "${CONDA_PREFIX}/bin/python" ]; then
  PY_BIN="${CONDA_PREFIX}/bin/python"
fi
if [ -z "${PY_BIN}" ] && command -v mlagents-learn >/dev/null 2>&1; then
  shebang="$(head -1 "$(command -v mlagents-learn)" | sed 's/^#! *//')"
  if [ -n "${shebang}" ] && [ -x "${shebang}" ]; then
    PY_BIN="${shebang}"
  fi
fi
if [ -z "${PY_BIN}" ] || ! command -v "${PY_BIN}" >/dev/null 2>&1; then
  if command -v python3 >/dev/null 2>&1; then
    PY_BIN="python3"
  else
    PY_BIN="python"
  fi
fi

raw="${1%/}"
case "${raw}" in
  results/*)
    RUN_DIR="${raw}"
    ;;
  *)
    RUN_DIR="results/${raw}"
    ;;
esac

if [ ! -d "${RUN_DIR}" ]; then
  echo "ERROR: каталог не найден: ${RUN_DIR}"
  exit 1
fi

promote_one_behavior_dir() {
  local beh_dir="$1"
  local best_file=""
  local best_step=""

  # Find newest VALID checkpoint by trying torch.load (descending by step).
  # This avoids the common "PytorchStreamReader ... file not found" when the latest .pt is truncated/corrupt.
  best_file="$(
    "${PY_BIN}" - <<'PY' "${beh_dir}" 2>/dev/null
import glob, os, re, sys

beh_dir = sys.argv[1]
rx = re.compile(r"^(.+)-(\d+)\.pt$")

cands = []
for p in glob.glob(os.path.join(beh_dir, "*.pt")):
    b = os.path.basename(p)
    if b in ("checkpoint.pt", "checkpoint.pt.tmp"):
        continue
    m = rx.match(b)
    if not m:
        continue
    cands.append((int(m.group(2)), p))

cands.sort(reverse=True, key=lambda x: x[0])

if not cands:
    sys.exit(1)

try:
    import torch
except ImportError:
    step, p = cands[0]
    print(f"{step}\t{p}")
    sys.exit(0)

for step, p in cands:
    try:
        try:
            torch.load(p, map_location="cpu", weights_only=False)
        except TypeError:
            torch.load(p, map_location="cpu")
        print(f"{step}\t{p}")
        sys.exit(0)
    except Exception:
        continue
sys.exit(2)
PY
  )" || true

  if [ -z "${best_file}" ]; then
    # Fallback: max step by filename (no torch validation).
    best_file="$(
      "${PY_BIN}" - <<'PY' "${beh_dir}" 2>/dev/null
import glob, os, re, sys

beh_dir = sys.argv[1]
rx = re.compile(r"^(.+)-(\d+)\.pt$")
cands = []
for p in glob.glob(os.path.join(beh_dir, "*.pt")):
    m = rx.match(os.path.basename(p))
    if m:
        cands.append((int(m.group(2)), p))
if not cands:
    sys.exit(1)
step, p = max(cands, key=lambda x: x[0])
print(f"{step}\t{p}")
PY
    )" || true
  fi

  if [ -z "${best_file}" ]; then
    echo "[promote] skip (нет валидных *-<steps>.pt): ${beh_dir}"
    return 1
  fi

  best_step="${best_file%%$'\t'*}"
  best_file="${best_file#*$'\t'}"

  local dst="${beh_dir}/checkpoint.pt"
  if [ -f "${dst}" ]; then
    cp -f "${dst}" "${dst}.bak_$(date +%Y%m%d%H%M%S)"
  fi
  cp -f "${best_file}" "${dst}"
  echo "[promote] OK $(basename "${beh_dir}") step=${best_step}"
  echo "         ${best_file}"
  echo "    ->   ${dst}"
  return 0
}

echo "[promote] run dir: ${RUN_DIR}"

promoted=0
for beh_dir in "${RUN_DIR}"/*/; do
  [ -d "${beh_dir}" ] || continue
  name="$(basename "${beh_dir%/}")"
  if [ "${name}" = "videos" ]; then
    continue
  fi
  if promote_one_behavior_dir "${beh_dir}"; then
    promoted=$((promoted + 1))
  fi
done

if [ "${promoted}" -eq 0 ]; then
  echo "[promote] ERROR: ни одна папка поведения не обновлена (нет *-<steps>.pt?)"
  exit 1
fi

echo "[promote] готово. Дальше: mlagents-learn ... --resume --run-id <тот же run>"
