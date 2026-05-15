# Copyright 2025 Bytedance Ltd. and/or its affiliates
#
# This code is inspired by the torchtune.
# https://github.com/pytorch/torchtune/blob/main/torchtune/utils/_device.py
#
# Copyright (c) Meta Platforms, Inc. and affiliates.
# All rights reserved.
#
# This source code is licensed under the BSD-style license in https://github.com/pytorch/torchtune/blob/main/LICENSE

import logging
import os

import torch

logger = logging.getLogger(__name__)


def is_torch_npu_available() -> bool:
    """Check the availability of NPU"""
    try:
        import torch_npu  # noqa: F401

        return torch.npu.is_available()
    except ImportError:
        return False


class _LazyDeviceFlag:
    """Re-evaluate availability on each check (bool/if), not at import time.

    Ray workers and driver import the same modules in different orders; a one-shot
    `torch.cuda.is_available()` at import time can stay false in GPU workers.
    """

    def __init__(self, fn):
        self._fn = fn

    def __bool__(self) -> bool:
        return bool(self._fn())


is_cuda_available = _LazyDeviceFlag(lambda: torch.cuda.is_available())
is_npu_available = _LazyDeviceFlag(is_torch_npu_available)


def sanitize_accelerator_env() -> None:
    """Fix GPU env vars that break CUDA inside nvidia-docker.

    ``docker run --gpus device=3,4`` often leaves ``NVIDIA_VISIBLE_DEVICES=3,4`` in the
    container. PyTorch then looks for GPU indices 3/4 while only cuda:0,1 exist →
    ``torch.cuda.is_available()`` is false and NCCL init fails.

    Call on the training driver before ``ray.init()`` and at the start of each GPU worker.
    """
    if os.environ.get("AENT_DISABLE_GPU_ENV_SANITIZE") == "1":
        return

    in_docker = os.path.exists("/.dockerenv") or os.environ.get("AENT_IN_DOCKER") == "1"
    if in_docker and "NVIDIA_VISIBLE_DEVICES" in os.environ:
        logger.info(
            "Unset NVIDIA_VISIBLE_DEVICES=%r (host GPU indices are invalid inside the container)",
            os.environ.get("NVIDIA_VISIBLE_DEVICES"),
        )
        del os.environ["NVIDIA_VISIBLE_DEVICES"]

    # Driver only: let Ray assign GPUs. Do not clear per-actor CUDA_VISIBLE_DEVICES from Ray.
    is_ray_worker = os.environ.get("RAY_LOCAL_RANK") is not None
    if not is_ray_worker and os.environ.get("AENT_KEEP_CUDA_VISIBLE_DEVICES") != "1":
        cvd = os.environ.get("CUDA_VISIBLE_DEVICES")
        if cvd is not None and not str(cvd).strip():
            del os.environ["CUDA_VISIBLE_DEVICES"]


def prime_ray_worker_cuda() -> None:
    """Best-effort CUDA init for Ray GPU actors.

    Some Ray / container setups leave `torch.cuda.is_available()` false until the
    runtime is explicitly probed, which breaks `init_process_group` (NCCL).
    """
    sanitize_accelerator_env()
    try:
        if hasattr(torch.cuda, "is_available"):
            # Clear sticky CUDA errors from earlier failed device_count() probes.
            try:
                torch.cuda.is_available()
            except Exception:
                pass
        n = torch.cuda.device_count()
        if n <= 0:
            return
        idx = int(os.environ.get("RAY_LOCAL_RANK", os.environ.get("LOCAL_RANK", "0")))
        idx = max(0, min(idx, n - 1))
        torch.cuda.set_device(idx)
        _ = torch.empty(1, device=f"cuda:{idx}")
    except Exception as e:  # pragma: no cover
        logger.warning("prime_ray_worker_cuda failed: %s", e)


def get_device_name() -> str:
    """Function that gets the torch.device based on the current machine.
    This currently only supports CPU, CUDA, NPU.
    Returns:
        device
    """
    if is_cuda_available:
        device = "cuda"
    elif is_npu_available:
        device = "npu"
    else:
        device = "cpu"
    return device


def get_torch_device() -> any:
    """Return the corresponding torch attribute based on the device type string.
    Returns:
        module: The corresponding torch device namespace, or torch.cuda if not found.
    """
    device_name = get_device_name()
    try:
        return getattr(torch, device_name)
    except AttributeError:
        logger.warning(f"Device namespace '{device_name}' not found in torch, try to load torch.cuda.")
        return torch.cuda


def get_device_id() -> int:
    """Return current device id based on the device type.
    Returns:
        device index
    """
    return get_torch_device().current_device()


def get_nccl_backend() -> str:
    """Return nccl backend type based on the device type.
    Returns:
        nccl backend type string.
    """
    if is_cuda_available:
        return "nccl"
    elif is_npu_available:
        return "hccl"
    else:
        raise RuntimeError(
            "No available nccl backend found on device type "
            f"{get_device_name()} (torch.cuda.is_available()={torch.cuda.is_available()}, "
            f"device_count={torch.cuda.device_count()}, "
            f"CUDA_VISIBLE_DEVICES={os.environ.get('CUDA_VISIBLE_DEVICES')}, "
            f"NVIDIA_VISIBLE_DEVICES={os.environ.get('NVIDIA_VISIBLE_DEVICES')}). "
            "Inside Docker: unset NVIDIA_VISIBLE_DEVICES (host indices like 3,4 break CUDA); "
            "run with `docker run --gpus '\"device=3,4\"'` and `unset CUDA_VISIBLE_DEVICES` before training; "
            "verify `nvidia-smi` and `python -c 'import torch; print(torch.cuda.device_count())'` in the container."
        )
