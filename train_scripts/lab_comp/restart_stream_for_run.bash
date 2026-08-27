#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "${ROOT}"
RUN_ID="${RUN_ID:-run_80}"

mkdir -p "stream_weights/${RUN_ID}/onnx"
for b in JackLowLevelAgent LilyLowLevelAgent GeorgeLowLevelAgent; do
  src="$(ls -1 "results/${RUN_ID}/${b}/"${b}-*.onnx 2>/dev/null | sort -V | tail -1 || true)"
  if [ -z "${src}" ] && [ "${b}" = "GeorgeLowLevelAgent" ]; then
    src="$(ls -1 results/run_74/GeorgeLowLevelAgent/*.onnx 2>/dev/null | sort -V | tail -1 || true)"
    echo "WARN: George из run_74 (временно), пока train не напишет onnx"
  fi
  if [ -n "${src}" ]; then
    cp -f "${src}" "stream_weights/${RUN_ID}/onnx/${b}.onnx"
    echo "sticky ${b} <- $(basename "${src}")"
  else
    echo "WARN: нет ${b}"
  fi
done
rm -f stream_weights/run_78/onnx/*.onnx 2>/dev/null || true

touch .stream_stop_request
sleep 2
pkill -f 'stream_onnx_infer\.py' 2>/dev/null || true
pkill -f 'forestStreamOnly' 2>/dev/null || true
sleep 2
rm -f .stream_stop_request .stream_restart_request

LOG="results/stream_onnx_${RUN_ID}.log"
nohup env RUN_ID="${RUN_ID}" bash train_scripts/lab_comp/run_stream_onnx.bash >"${LOG}" 2>&1 &
echo "started pid=$! log=${LOG}"
sleep 10
tail -30 "${LOG}"

# Unity после рестарта пустой — подтянуть roster из SQLite (бот :8765).
echo "[restart_stream] roster resync → Unity…"
for i in $(seq 1 15); do
  if curl -sf -X POST "http://127.0.0.1:8765/roster/resync" >/tmp/roster_resync.json 2>/dev/null; then
    echo "[restart_stream] resync ok ($(python3 -c 'import json;print(json.load(open("/tmp/roster_resync.json")).get("roster_count","?"))' 2>/dev/null || echo '?') players)"
    break
  fi
  echo "[restart_stream] resync wait ${i}/15 (bot/Unity ещё не готовы)"
  sleep 8
done
# Повтор через 30с — Unity иногда поднимается позже UDP.
sleep 30
curl -sf -X POST "http://127.0.0.1:8765/roster/resync" >/tmp/roster_resync2.json 2>/dev/null \
  && echo "[restart_stream] resync2 ok ($(python3 -c 'import json;print(json.load(open("/tmp/roster_resync2.json")).get("roster_count","?"))' 2>/dev/null || echo '?') players)" \
  || echo "[restart_stream] resync2 skipped"
