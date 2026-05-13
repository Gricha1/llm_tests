#!/usr/bin/env bash
set -euo pipefail

IMAGE_NAME="${1:-aent_titan}"

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "${REPO_ROOT}"

echo "Building docker image: ${IMAGE_NAME}"
docker build -t "${IMAGE_NAME}" -f docker/Dockerfile.titan .
