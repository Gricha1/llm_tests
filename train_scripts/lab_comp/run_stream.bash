#!/usr/bin/env bash
# Стрим OBS: Unity + onnxruntime (не sentis).
echo "[run_stream] → run_stream_onnx.bash"
exec bash "$(cd "$(dirname "$0")" && pwd)/run_stream_onnx.bash" "$@"
