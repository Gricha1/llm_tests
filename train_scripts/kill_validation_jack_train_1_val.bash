#!/usr/bin/env bash
# Принудительно гасит ВАЛИДАЦИЮ (inference/video watcher) только для среды jack_train_1_val.
# Не трогает обучение на jack_train_1 и другие среды.
#
# Запуск:
#   bash train_scripts/kill_validation_jack_train_1_val.bash
#
# Ориентируется по подстроке "jack_train_1_val" в командной строке процессов.

set +e

TARGET="jack_train_1_val"

echo "[kill-val] watcher validate_video_watcher_fixed.bash (${TARGET})..."
pkill -9 -f "validate_video_watcher_fixed.bash.*${TARGET}" 2>/dev/null
pkill -9 -f "validate_video_watcher_fixed.sh.*${TARGET}" 2>/dev/null

echo "[kill-val] mlagents-learn (${TARGET})..."
pkill -9 -f "mlagents-learn.*${TARGET}" 2>/dev/null

# Unity-сборки именно этой среды (build_versions/jack_train_1_val)
echo "[kill-val] Unity build_versions/${TARGET}..."
pkill -9 -f "build_versions/${TARGET}" 2>/dev/null
pkill -9 -f "build_versions\\${TARGET}" 2>/dev/null
pkill -9 -f "unity_projects/forest_survival/build_versions/${TARGET}" 2>/dev/null
pkill -9 -f "unity_projects\\forest_survival\\build_versions\\${TARGET}" 2>/dev/null

sleep 0.3

echo ""
echo "[kill-val] осталось что-то с ${TARGET} в командной строке:"
ps aux 2>/dev/null | grep -E "${TARGET}|mlagents-learn|validate_video_watcher_fixed" | grep -v grep || echo "  (ничего)"

echo ""
echo "Готово. Если PID всё ещё в htop — добей вручную: kill -9 <PID>"
