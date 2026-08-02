#!/usr/bin/env bash
# Убить только train (mlagents + headless Unity + UI-слот), стрим (forestStreamOnly) не трогать.
#
#   bash train_scripts/lab_comp/kill_train.bash
#   KEEP_TENSORBOARD=1 bash train_scripts/lab_comp/kill_train.bash
set -eu
set -o pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")/../.." && pwd)"
cd "${ROOT}"

echo "[kill_train] останавливаю mlagents-learn и headless Unity..."

kill_ui_slot() {
  local session="$1"
  local pidf="/tmp/forest_ui_${session}.pid"
  if [ -f "$pidf" ]; then
    local old
    old="$(tr -d ' \r\n' <"$pidf" || true)"
    if [ -n "${old:-}" ]; then
      echo "[kill_train] UI slot ${session} pid=${old}"
      kill -TERM "$old" 2>/dev/null || true
      sleep 1
      kill -KILL "$old" 2>/dev/null || true
      # дочерние от setsid
      pkill -TERM -P "$old" 2>/dev/null || true
      pkill -KILL -P "$old" 2>/dev/null || true
    fi
    rm -f "$pidf"
  fi
  tmux has-session -t "${session}" 2>/dev/null && tmux kill-session -t "${session}" || true
}

# 0) UI detached-слоты train (НЕ fui_joint_stream)
for s in fui_joint_jlg \
  fui_jack_s1 fui_jack_s2 fui_jack_cur \
  fui_lily_s1 fui_lily_s2 fui_lily_cur \
  fui_george_s1 fui_george_s2 fui_george_cur
do
  kill_ui_slot "$s"
done

# 1) trainer
pkill -TERM -f 'mlagents-learn' 2>/dev/null || true
sleep 1
pkill -KILL -f 'mlagents-learn' 2>/dev/null || true

# 2) headless train Unity: batchmode / TrainAllHeadless / launch через env
#    НЕ трогаем forestStreamOnly (стрим OBS).
pkill -TERM -f 'forestTrainAllHeadless' 2>/dev/null || true
pkill -TERM -f 'stream_forest_survival_.*\.x86_64.*-batchmode' 2>/dev/null || true
pkill -TERM -f 'build_versions/.*\.x86_64.*-batchmode' 2>/dev/null || true
pkill -TERM -f 'train_headless_jack_lily_george' 2>/dev/null || true
pkill -TERM -f 'train_headless_(jack|lily|george)_stage' 2>/dev/null || true
sleep 2
pkill -KILL -f 'forestTrainAllHeadless' 2>/dev/null || true
pkill -KILL -f 'stream_forest_survival_.*\.x86_64.*-batchmode' 2>/dev/null || true
pkill -KILL -f 'build_versions/.*\.x86_64.*-batchmode' 2>/dev/null || true
pkill -KILL -f 'train_headless_jack_lily_george' 2>/dev/null || true
pkill -KILL -f 'train_headless_(jack|lily|george)_stage' 2>/dev/null || true

# 3) обёртка launcher, если зависла без Unity
pkill -TERM -f 'launch_forest_env\.bash' 2>/dev/null || true
pkill -KILL -f 'launch_forest_env\.bash' 2>/dev/null || true

if [ "${KEEP_TENSORBOARD:-0}" != "1" ]; then
  pkill -TERM -f 'tensorboard.*forest_survival/results' 2>/dev/null || true
  sleep 1
  pkill -KILL -f 'tensorboard.*forest_survival/results' 2>/dev/null || true
fi

sleep 1

n_ml=0; n_ht=0; n_bm=0; n_wrap=0
n_ml="$(pgrep -c -f 'mlagents-learn' 2>/dev/null || true)"
n_ht="$(pgrep -c -f 'forestTrainAllHeadless' 2>/dev/null || true)"
n_bm="$(pgrep -c -f 'stream_forest_survival_.*\.x86_64.*-batchmode' 2>/dev/null || true)"
n_wrap="$(pgrep -c -f 'train_headless_jack_lily_george' 2>/dev/null || true)"
n_ml="${n_ml:-0}"
n_ht="${n_ht:-0}"
n_bm="${n_bm:-0}"
n_wrap="${n_wrap:-0}"

echo "[kill_train] осталось: mlagents=${n_ml} trainAllHeadless=${n_ht} batchmode_build=${n_bm} joint_wrap=${n_wrap}"
free -h | awk '/Mem:/{printf "[kill_train] RAM available≈%s / %s\n", $7, $2}'
