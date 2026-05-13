#!/usr/bin/env bash
set -euo pipefail

# Usage:
#   bash docker/start.sh [container_name] [image_name] [gpu_list]
#
# Defaults:
# - container_name: aent_titan
# - image_name: aent_titan
# - gpu_list: 3,4   (your request: occupy TITAN RTX #3 and #4)
#
# This container is intended to be self-contained:
# - Models are downloaded from HuggingFace by default (see run_train script).
# - Datasets are prepared inside the mounted repo folder on first run.

CONTAINER_NAME="${1:-aent_titan}"
IMAGE_NAME="${2:-aent_titan}"
GPU_LIST="${3:-3,4}"

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

echo "Starting container: ${CONTAINER_NAME}"
echo "Image: ${IMAGE_NAME}"
echo "GPUs (host indices): ${GPU_LIST}"
echo "Mount: ${REPO_ROOT} -> /workspace/aent"
extra_mounts=()

# IMPORTANT:
# For multiple GPU ids, docker expects the argument to include inner quotes:
#   --gpus '"device=3,4"'
exec docker run -it --rm \
  --name "${CONTAINER_NAME}" \
  --gpus "\"device=${GPU_LIST}\"" \
  --shm-size=64g \
  --ipc=host \
  -e NVIDIA_DRIVER_CAPABILITIES=compute,utility \
  -e PYTORCH_CUDA_ALLOC_CONF=expandable_segments:True \
  -e HF_HOME=/workspace/.cache/huggingface \
  -e TRANSFORMERS_CACHE=/workspace/.cache/huggingface \
  -e WANDB_DIR=/workspace/.cache/wandb \
  -v "${REPO_ROOT}:/workspace/aent" \
  -v "${REPO_ROOT}/.cache:/workspace/.cache" \
  "${extra_mounts[@]}" \
  "${IMAGE_NAME}" \
  bash
