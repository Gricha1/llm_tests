#!/usr/bin/env bash
set -euo pipefail

# Usage:
#   bash docker/start_a100.sh [container_name] [image_name] [gpu_list]
#
# Defaults (this host: 2x NVIDIA A100 80GB PCIe):
# - container_name: aent_a100
# - image_name: aent_a100
# - gpu_list: 0,1
#
# Examples:
#   bash docker/start_a100.sh
#   bash docker/start_a100.sh aent_a100 aent_a100 0
#   bash docker/start_a100.sh my_run aent_a100 0,1

CONTAINER_NAME="${1:-aent_a100}"
IMAGE_NAME="${2:-aent_a100}"
GPU_LIST="${3:-0,1}"

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

echo "Starting A100 container: ${CONTAINER_NAME}"
echo "Image: ${IMAGE_NAME}"
echo "GPUs (host indices): ${GPU_LIST}"
echo "Mount: ${REPO_ROOT} -> /workspace/aent"
extra_mounts=()

# For multiple GPU ids, docker expects inner quotes: --gpus '"device=0,1"'
exec docker run -it --rm \
  --name "${CONTAINER_NAME}" \
  --gpus "\"device=${GPU_LIST}\"" \
  --shm-size=64g \
  --ipc=host \
  -e NVIDIA_DRIVER_CAPABILITIES=compute,utility \
  -e AENT_IN_DOCKER=1 \
  -e PYTORCH_CUDA_ALLOC_CONF=expandable_segments:True \
  -e HF_HOME=/workspace/.cache/huggingface \
  -e TRANSFORMERS_CACHE=/workspace/.cache/huggingface \
  -e WANDB_DIR=/workspace/.cache/wandb \
  -v "${REPO_ROOT}:/workspace/aent" \
  -v "${REPO_ROOT}/.cache:/workspace/.cache" \
  "${extra_mounts[@]}" \
  "${IMAGE_NAME}" \
  bash
