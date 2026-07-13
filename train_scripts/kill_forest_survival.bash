#!/usr/bin/env bash
# Принудительно гасит обучение/инференс ML-Agents и Unity-сборки .x86_64 из build_versions этого проекта (WSL/Linux).
# Запуск:
#   bash train_scripts/kill_forest_survival.bash
#
# Важно: файл должен быть с окончаниями строк LF (Unix). CRLF даёт ошибку вида: $'\r': command not found

set +e

kill_by_regex() {
  local desc="$1"
  local pattern="$2"
  local pid
  while read -r pid; do
    [ -z "${pid}" ] && continue
    echo "[kill] ${desc} PID=${pid}"
    kill -9 "${pid}" 2>/dev/null || true
  done < <(pgrep -f "$pattern" 2>/dev/null || true)
}

echo "[kill] mlagents-learn..."
pkill -9 -f mlagents-learn 2>/dev/null || true

echo "[kill] sync_stream_weights / sentis export..."
pkill -9 -f sync_stream_weights 2>/dev/null || true
pkill -9 -f export_stream_sentis 2>/dev/null || true
pkill -9 -f export_stream_sentis_glibc 2>/dev/null || true
pkill -9 -f 'Editor/Unity' 2>/dev/null || true
pkill -9 -f 'ld-linux.*Unity' 2>/dev/null || true

echo "[kill] validate_video_watcher..."
pkill -9 -f validate_video_watcher_fixed.bash 2>/dev/null || true

echo "[kill] Unity .x86_64 (pgrep + kill, надёжнее одного pkill)..."
# Любой бинарник под .../forest_survival/build_versions/*.x86_64
kill_by_regex "build_versions/*.x86_64" '[/]forest_survival/build_versions/[^ ]+\.x86_64'

echo "[kill] Unity (доп. pkill по путям проекта)..."
pkill -9 -f 'forest_survival/build_versions' 2>/dev/null || true
pkill -9 -f 'unity_projects/forest_survival/build_versions' 2>/dev/null || true
# Старые пути с пробелами в имени папки/билда (если остались процессы)
pkill -9 -f 'forest_survival/build versions' 2>/dev/null || true

sleep 0.4

echo ""
echo "[kill] осталось что-то с forest_survival / mlagents в командной строке:"
ps aux 2>/dev/null | grep -E 'forest_survival|mlagents-learn' | grep -v grep || echo "  (ничего)"

echo ""
echo "Готово. Если PID всё ещё в htop: kill -9 <PID>"
