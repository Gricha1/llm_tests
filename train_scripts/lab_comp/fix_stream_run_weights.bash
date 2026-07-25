#!/usr/bin/env bash
# Починить стрим под текущий train: скопировать onnx run→sticky и подсказать RUN_ID.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "${ROOT}"
RUN_ID="${RUN_ID:-run_80}"
mkdir -p "stream_weights/${RUN_ID}/onnx"
for b in JackLowLevelAgent LilyLowLevelAgent GeorgeLowLevelAgent; do
  src="$(ls -1 "results/${RUN_ID}/${b}/"${b}-*.onnx 2>/dev/null | sort -V | tail -1 || true)"
  if [ -z "${src}" ]; then
    echo "WARN: нет onnx для ${b} в results/${RUN_ID}"
    continue
  fi
  cp -f "${src}" "stream_weights/${RUN_ID}/onnx/${b}.onnx"
  echo "sticky ${b} <- $(basename "${src}")"
done
# Старый sticky run_78 с obs=14 ломает новый билд — убрать.
rm -f stream_weights/run_78/onnx/*.onnx 2>/dev/null || true
echo "Перезапусти стрим:"
echo "  RUN_ID=${RUN_ID} bash train_scripts/lab_comp/run_stream_onnx.bash"
