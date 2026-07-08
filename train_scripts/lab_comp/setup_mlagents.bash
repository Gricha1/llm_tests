#!/usr/bin/env bash
# Запускать НА lab_comp (или: ssh lab_comp 'bash -s' < train_scripts/lab_comp/setup_mlagents.bash)
set -eu
set -o pipefail

PY_VER="3.10.12"
MLA_VER="1.1.0"

if [ -f "${HOME}/anaconda3/etc/profile.d/conda.sh" ]; then
  # shellcheck source=/dev/null
  source "${HOME}/anaconda3/etc/profile.d/conda.sh"
else
  echo "ERROR: conda не найдена в ~/anaconda3" >&2
  exit 1
fi

if ! conda env list | awk '{print $1}' | grep -qx mlagents; then
  echo "[lab_comp] создаём conda env mlagents (python=${PY_VER})"
  conda create -y -n mlagents "python=${PY_VER}" pip
fi

conda activate mlagents

actual_py="$(python -c 'import sys; print(".".join(map(str, sys.version_info[:3])))')"
echo "[lab_comp] python=${actual_py}"

echo "[lab_comp] pip install mlagents==${MLA_VER}"
pip install -U pip
# Ubuntu 18.04: системный HDF5 1.10.0; setuptools 82+ убирает pkg_resources
pip install "h5py==3.11.0" "setuptools<81"
# Драйвер 470 на Ubuntu 18.04: torch cu124 не видит GPU; cu117 работает
pip install "torch==2.0.1+cu117" "torchvision==0.15.2+cu117" \
  --index-url https://download.pytorch.org/whl/cu117
pip install "mlagents==${MLA_VER}"

echo "[lab_comp] готово:"
mlagents-learn --help | head -3
echo "Активация: conda activate mlagents"
