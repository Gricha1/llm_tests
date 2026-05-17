#!/usr/bin/env bash
set -euo pipefail

# Usage:
#   bash docker/build_a100.sh [image_name]
#
# Default image: aent_a100

IMAGE_NAME="${1:-aent_a100}"

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "${REPO_ROOT}"

echo "Building A100 docker image: ${IMAGE_NAME}"
echo "Dockerfile: docker/Dockerfile.a100"
docker build -t "${IMAGE_NAME}" -f docker/Dockerfile.a100 .
