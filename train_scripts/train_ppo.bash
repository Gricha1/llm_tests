#!/usr/bin/env bash
set -eu
set -o pipefail

# Всегда корень репозитория (не зависит от того, откуда вызвали скрипт).
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")/.." && pwd)"
cd "${ROOT}"

# $1 — папка билда под build_versions/ (например jack_lily_train_together.x86_64)
# $2 — num-envs
# $3 — имя yaml в custom_configs/ без пути (например Jack_Lily)
#
# Режимы по числу аргументов:
#   3 аргумента: новый run, свободный results/run_N
#   4 аргумента: --resume --run-id $4 (как раньше; оба агента из одной папки run)
#   5 аргументов: совместный старт Jack+Lily с разными источниками весов (без --resume):
#                  $4 = run_id источника весов Лили (results/$4/LilyLowLevelAgent/*.pt)
#                  $5 = run_id источника весов Джека (results/$5/JackLowLevelAgent/*.pt)
#                  новый run_id под результаты выбирается автоматически (первый свободный run_N)
#   6 аргументов: то же, но $6 = явный run_id для записи чекпоинтов (results/$6/...)

PY_BIN="python3"
if ! command -v python3 >/dev/null 2>&1; then
  PY_BIN="python"
fi

find_behavior_checkpoint() {
  # Печатает путь к .pt для resume/init: предпочитает checkpoint.pt, иначе макс. step по имени *-<step>.pt
  local run_id="$1"
  local behavior_name="$2"
  "${PY_BIN}" - <<'PY' "${ROOT}" "${run_id}" "${behavior_name}"
import os, re, sys
root, run_id, behavior = sys.argv[1], sys.argv[2], sys.argv[3]
d = os.path.join(root, "results", run_id, behavior)
if not os.path.isdir(d):
    print(f"ERROR: нет папки: {d}", file=sys.stderr)
    sys.exit(1)
ck = os.path.join(d, "checkpoint.pt")
if os.path.isfile(ck):
    print(os.path.abspath(ck))
    sys.exit(0)
best_p = None
best_s = -1
rx = re.compile(r"^" + re.escape(behavior) + r"-(\d+)\.pt$")
for name in os.listdir(d):
    m = rx.match(name)
    if m:
        s = int(m.group(1))
        if s > best_s:
            best_s = s
            best_p = os.path.join(d, name)
if best_p is None:
    print(f"ERROR: в {d} нет checkpoint.pt и ни одного {behavior}-<step>.pt", file=sys.stderr)
    sys.exit(1)
print(os.path.abspath(best_p))
PY
}

write_yaml_with_init_paths() {
  local src_yaml="$1"
  local dst_yaml="$2"
  local jack_pt="$3"
  local lily_pt="$4"
  "${PY_BIN}" - <<'PY' "${src_yaml}" "${dst_yaml}" "${jack_pt}" "${lily_pt}"
import os, sys
src, dst, jack_pt, lily_pt = sys.argv[1], sys.argv[2], sys.argv[3], sys.argv[4]
text = open(src, "r", encoding="utf-8").read().splitlines()
# Пути для YAML: относительно корня проекта, без обратных слешей
def rel(p):
    p = os.path.abspath(p)
    root = os.path.abspath(os.path.join(os.path.dirname(dst), ".."))
    try:
        return os.path.relpath(p, root).replace("\\", "/")
    except ValueError:
        return p.replace("\\", "/")

jack_r = rel(jack_pt)
lily_r = rel(lily_pt)
behavior = None
jack_done = False
lily_done = False
out_lines = []
for line in text:
    s = line.strip()
    if s.startswith("JackLowLevelAgent:"):
        behavior = "jack"
    elif s.startswith("LilyLowLevelAgent:"):
        behavior = "lily"
    elif s == "init_path: null" and line.startswith("    ") and behavior == "jack" and not jack_done:
        line = f"    init_path: {jack_r}"
        jack_done = True
    elif s == "init_path: null" and line.startswith("    ") and behavior == "lily" and not lily_done:
        line = f"    init_path: {lily_r}"
        lily_done = True
    out_lines.append(line)
if not jack_done or not lily_done:
    print("ERROR: в yaml не найдены оба блока init_path: null (Jack / Lily).", file=sys.stderr)
    sys.exit(1)
open(dst, "w", encoding="utf-8").write("\n".join(out_lines) + "\n")
print(f"[train_ppo] wrote {dst}")
print(f"[train_ppo]   Jack init_path -> {jack_r}")
print(f"[train_ppo]   Lily init_path -> {lily_r}")
PY
}

# $1 - environment folde
# $2 - env num
# $3 - config name (yaml in custom_configs/)

if [ -z "${1:-}" ]; then
  echo "set environmet in first arg!!!"
  exit 1
fi
build_folder="build_versions/$1"
echo "environment name: $build_folder"

START_PORT=5005
CHECK_COUNT=1000
for i in $(seq 0 "${CHECK_COUNT}"); do
  PORT=$((START_PORT + i))
  if ! netstat -tuln 2>/dev/null | grep -q ":${PORT} "; then
    echo "start on port: $PORT"
    break
  fi
done

if [ -z "${2:-}" ]; then
  echo "set num envs in second arg!!!"
  exit 1
fi
num_envs="$2"
echo "num envs: ${num_envs}"

if [ -z "${3:-}" ]; then
  echo "set config name!!!"
  exit 1
fi
config_name="$3"
echo "config: ${config_name}"

dir_name="results"
base_name="run"
count=1000000
base_yaml="custom_configs/${config_name}.yaml"
if [ ! -f "${base_yaml}" ]; then
  echo "ERROR: нет файла ${base_yaml}"
  exit 1
fi

pick_free_run_id() {
  local i
  for ((i = 1; i <= count; i++)); do
    local folder_name="${dir_name}/${base_name}_${i}"
    if [ ! -d "${folder_name}" ]; then
      echo "${base_name}_${i}"
      return 0
    fi
  done
  echo "ERROR: не нашли свободный ${dir_name}/${base_name}_N за разумный предел" >&2
  exit 1
}

# 5+ аргументов: совместный старт с init_path из двух разных run (Лили $4, Джек $5)
if [ -n "${5:-}" ]; then
  lily_src_run="$4"
  jack_src_run="$5"
  if [ -n "${6:-}" ]; then
    exp_foder="$6"
  else
    exp_foder="$(pick_free_run_id)"
  fi
  echo "[train_ppo] joint bootstrap: Lily weights from results/${lily_src_run}/LilyLowLevelAgent"
  echo "[train_ppo] joint bootstrap: Jack weights from results/${jack_src_run}/JackLowLevelAgent"
  echo "[train_ppo] new run-id (output): ${exp_foder}"

  lily_pt="$(find_behavior_checkpoint "${lily_src_run}" "LilyLowLevelAgent")"
  jack_pt="$(find_behavior_checkpoint "${jack_src_run}" "JackLowLevelAgent")"

  autogen_yaml="custom_configs/_autogen_${config_name}.yaml"
  write_yaml_with_init_paths "${base_yaml}" "${autogen_yaml}" "${jack_pt}" "${lily_pt}"

  mlagents-learn "${autogen_yaml}" --run-id "${exp_foder}" --env="${build_folder}" --base-port "${PORT}" --num-envs="${num_envs}" --no-graphics
  exit 0
fi

if [ -z "${4:-}" ]; then
  echo "new training"
  exp_foder="$(pick_free_run_id)"
  echo "exp folder: ${exp_foder}"
  mlagents-learn "${base_yaml}" --run-id "${exp_foder}" --env="${build_folder}" --base-port "${PORT}" --num-envs="${num_envs}" --no-graphics
else
  exp_foder="$4"
  echo "resume ${exp_foder}"
  mlagents-learn "${base_yaml}" --run-id "${exp_foder}" --env="${build_folder}" --resume --base-port "${PORT}" --num-envs="${num_envs}" --no-graphics
fi
