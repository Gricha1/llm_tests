#!/usr/bin/env bash
# Локальный presentation БЕЗ стрима/OBS/onnx.
#
# Ручной осмотр костра (окно + WASD):
#   bash train_scripts/run_presentation_manual_local.bash
#   Кнопка справа сверху / M — ручное; P — Jack↔Гера.
#
# Автопроверка костра (без окна, только лог):
#   bash train_scripts/run_presentation_manual_local.bash --campfire-test
set -eu
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")/.." && pwd)"
NAME="${BUILD_NAME:-presentation_manual_win}"
EXE="${ROOT}/build_versions/${NAME}/${NAME}.exe"

if [ ! -f "${EXE}" ]; then
  echo "[run] нет билда — собираю…"
  bash "${ROOT}/train_scripts/build_presentation_windows.bash" "${NAME}"
fi

EXE_WIN="$(wslpath -w "${EXE}" 2>/dev/null || echo "${EXE}")"
LOG="${ROOT}/build_versions/${NAME}/last_run.log"

if [ "${1:-}" = "--campfire-test" ]; then
  echo "[run] CAMPFIRE smoke-test (batchmode, без стрима)…"
  # cmd.exe чтобы Windows exe точно стартовал из WSL
  cmd.exe /c "\"${EXE_WIN}\" -batchmode -nographics -forestStreamOnly -forestCampfireTest -logFile \"$(wslpath -w "${LOG}")\"" || true
  echo "---- log ----"
  grep -E 'CAMPFIRE_TEST|error|Exception' "${LOG}" | tail -40 || tail -30 "${LOG}"
  if grep -q 'CAMPFIRE_TEST PASS' "${LOG}"; then
    echo "[run] PASS"
    exit 0
  fi
  echo "[run] FAIL — смотри ${LOG}"
  exit 2
fi

echo "[run] presentation manual (окно). Кнопка «Ручное» / M. WASD + ЛКМ."
echo "[run] exe: ${EXE}"
cmd.exe /c "start \"\" \"${EXE_WIN}\" -forestStreamOnly -forestPresentationManual"
