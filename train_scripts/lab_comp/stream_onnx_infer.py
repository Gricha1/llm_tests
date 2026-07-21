#!/usr/bin/env python3
"""Стрим-инференс: Unity (графика) + onnxruntime, без mlagents-learn и без .sentis.

  RUN_ID=run_72 bash train_scripts/lab_comp/run_stream_onnx.bash

Сам по себе (если Unity уже ждёт порт):
  python train_scripts/lab_comp/stream_onnx_infer.py --run-id run_72 --port 7000 --env build.x86_64
"""

import argparse
import os
import shutil
import sys
import tempfile
import threading
import time
from pathlib import Path
from typing import Dict, List, Optional, Tuple

import numpy as np

BEHAVIORS = (
    "JackLowLevelAgent",
    "LilyLowLevelAgent",
    "GeorgeLowLevelAgent",
)


def _log(msg: str) -> None:
    print(f"[stream_onnx] {msg}", flush=True)


def behavior_stem(behavior: str) -> str:
    """Unity отдаёт 'JackLowLevelAgent?team=0' — onnx лежит в папке без '?team='."""
    return behavior.split("?", 1)[0]


def _onnx_step(path: Path, name: str) -> int:
    """Номер шага из Name-123.onnx. Без номера / мусор → -1."""
    stem = path.stem  # JackLowLevelAgent-28444931
    prefix = f"{name}-"
    if not stem.startswith(prefix):
        return -1
    step_s = stem[len(prefix) :]
    if not step_s.isdigit():
        return -1
    return int(step_s)


def find_latest_onnx(run_dir: Path, behavior: str) -> Optional[Path]:
    """Самый свежий чекпоинт по номеру шага (не по mtime).

    ML-Agents иногда пишет Name-0.onnx рядом с checkpoint.pt — он новее по
    времени, но это не обученные веса (шаг 0). Берём max(step).
    """
    name = behavior_stem(behavior)
    d = run_dir / name
    if not d.is_dir():
        return None

    best: Optional[Path] = None
    best_step = -1
    for path in d.glob(f"{name}-*.onnx"):
        step = _onnx_step(path, name)
        if step > best_step:
            best_step = step
            best = path

    # step=0 только если других нет
    if best is not None and best_step > 0:
        return best
    if best is not None:
        return best

    plain = d / f"{name}.onnx"
    if plain.is_file():
        return plain
    root = run_dir / f"{name}.onnx"
    return root if root.is_file() else None


def _file_stable(path: Path, settle_sec: float = 0.4) -> bool:
    """Не грузим onnx, пока trainer его дописывает — иначе лаг + битый файл."""
    try:
        s1 = path.stat()
        if s1.st_size < 1024:
            return False
        time.sleep(settle_sec)
        s2 = path.stat()
        return s1.st_size == s2.st_size and s1.st_mtime == s2.st_mtime
    except OSError:
        return False


class OnnxPolicy:
    def __init__(self, path: Path):
        import onnxruntime as ort

        self.path = path
        self.mtime = path.stat().st_mtime
        # Копия: trainer может перезаписать файл во время InferenceSession().
        with tempfile.NamedTemporaryFile(suffix=".onnx", delete=False) as tmp:
            tmp_path = Path(tmp.name)
        try:
            shutil.copy2(path, tmp_path)
            so = ort.SessionOptions()
            so.intra_op_num_threads = 1
            so.inter_op_num_threads = 1
            so.graph_optimization_level = ort.GraphOptimizationLevel.ORT_ENABLE_ALL
            self.session = ort.InferenceSession(
                str(tmp_path),
                sess_options=so,
                providers=["CPUExecutionProvider"],
            )
        finally:
            try:
                tmp_path.unlink()
            except OSError:
                pass

        self.input_names = [i.name for i in self.session.get_inputs()]
        self.output_names = [o.name for o in self.session.get_outputs()]
        _log(f"load {path.name} in={self.input_names} out={self.output_names}")

    def matches(self, path: Path) -> bool:
        try:
            return self.path == path and self.path.stat().st_mtime == self.mtime
        except OSError:
            return False

    def act(self, obs_list: List[np.ndarray], action_masks: Optional[np.ndarray] = None) -> np.ndarray:
        """obs_list: list of (n_agents, obs_dim) from decision_steps.obs"""
        batch = int(obs_list[0].shape[0]) if obs_list[0].ndim > 1 else 1
        feeds = {}
        for i, name in enumerate(self.input_names):
            lname = name.lower()
            if "mask" in lname:
                if action_masks is not None:
                    feeds[name] = action_masks.astype(np.float32)
                else:
                    shape = self.session.get_inputs()[i].shape
                    mask_dim = 1
                    for d in shape[1:]:
                        if isinstance(d, int) and d > 0:
                            mask_dim = d
                            break
                    feeds[name] = np.ones((batch, mask_dim), dtype=np.float32)
            elif name.startswith("obs_") or "obs" in lname or i == 0:
                idx = 0
                if name.startswith("obs_"):
                    try:
                        idx = int(name.split("_", 1)[1])
                    except ValueError:
                        idx = 0
                arr = obs_list[min(idx, len(obs_list) - 1)].astype(np.float32)
                if arr.ndim == 1:
                    arr = arr[None, :]
                feeds[name] = arr
            else:
                shape = self.session.get_inputs()[i].shape
                dims = []
                for d in shape:
                    if isinstance(d, str) or d is None or d < 0:
                        dims.append(batch if not dims else 1)
                    else:
                        dims.append(int(d))
                feeds[name] = np.zeros(dims, dtype=np.float32)

        outs = self.session.run(None, feeds)
        by_name = dict(zip(self.output_names, outs))

        # Как в обучении / prefab (m_DeterministicInference=0): сэмпл из политики, не argmax.
        for key in (
            "discrete_actions",
            "deterministic_discrete_actions",
            "discrete_action",
        ):
            if key in by_name:
                act = np.asarray(by_name[key])
                if act.ndim == 1:
                    act = act[None, :]
                return act.astype(np.int32)

        for out in outs:
            arr = np.asarray(out)
            if np.issubdtype(arr.dtype, np.integer) or (
                np.issubdtype(arr.dtype, np.floating) and arr.size > 0 and arr.max() < 100
            ):
                if arr.ndim == 1:
                    arr = arr[None, :]
                return np.rint(arr).astype(np.int32)

        raise RuntimeError(f"не нашли discrete_actions в {self.output_names}")


class PolicyBank:
    """Веса грузятся в фоне. По умолчанию — только после конца эпизода (незаметно)."""

    def __init__(self, run_dir: Path, poll_sec: float = 0.0):
        self.run_dir = run_dir
        # 0 = только по request_reload() (конец эпизода); >0 = ещё и страховочный таймер.
        self.poll_sec = poll_sec
        self.policies: Dict[str, OnnxPolicy] = {}
        self._lock = threading.Lock()
        self._loading: set = set()
        self._stop = threading.Event()
        self._wake = threading.Event()
        self._thread = threading.Thread(
            target=self._loader_loop, name="onnx-reload", daemon=True
        )
        self._thread.start()

    def close(self) -> None:
        self._stop.set()
        self._wake.set()

    def ensure(self, behavior: str) -> Optional[OnnxPolicy]:
        key = behavior_stem(behavior)
        with self._lock:
            return self.policies.get(key)

    def request_reload(self) -> None:
        """Вызвать при terminal_steps — подтянуть свежие .onnx в фоне."""
        self._wake.set()

    def _reload_all_blocking(self) -> None:
        """Только до старта Unity — один раз можно подождать."""
        for behavior in BEHAVIORS:
            path = find_latest_onnx(self.run_dir, behavior)
            if path is None:
                continue
            if not _file_stable(path, settle_sec=0.2):
                continue
            try:
                pol = OnnxPolicy(path)
                with self._lock:
                    self.policies[behavior] = pol
            except Exception as e:
                _log(f"reload fail {behavior}: {e}")

    def ready(self) -> bool:
        with self._lock:
            return all(b in self.policies for b in BEHAVIORS)

    def _loader_loop(self) -> None:
        while not self._stop.is_set():
            timeout = self.poll_sec if self.poll_sec > 0 else None
            self._wake.wait(timeout=timeout)
            self._wake.clear()
            if self._stop.is_set():
                break

            jobs: List[Tuple[str, Path]] = []
            for behavior in BEHAVIORS:
                path = find_latest_onnx(self.run_dir, behavior)
                if path is None:
                    continue
                with self._lock:
                    cur = self.policies.get(behavior)
                    if cur is not None and cur.matches(path):
                        continue
                    if behavior in self._loading:
                        continue
                    self._loading.add(behavior)
                jobs.append((behavior, path))

            if not jobs:
                continue

            for behavior, path in jobs:
                try:
                    if not _file_stable(path, settle_sec=0.35):
                        continue
                    latest = find_latest_onnx(self.run_dir, behavior) or path
                    if latest != path and not _file_stable(latest, settle_sec=0.35):
                        continue
                    pol = OnnxPolicy(latest)
                    with self._lock:
                        self.policies[behavior] = pol
                    _log(f"weights updated: {behavior} <- {latest.name}")
                except Exception as e:
                    _log(f"reload fail {behavior}: {e}")
                finally:
                    with self._lock:
                        self._loading.discard(behavior)


def run_loop(
    env,
    bank: PolicyBank,
    step_log_every: int = 500,
    target_fps: float = 30.0,
) -> None:
    from mlagents_envs.base_env import ActionTuple

    behavior_names = list(env.behavior_specs.keys())
    jack_keys = [b for b in behavior_names if behavior_stem(b) == "JackLowLevelAgent"]
    _log(f"behaviors from Unity: {behavior_names}")
    for behavior in behavior_names:
        pol = bank.ensure(behavior)
        if pol is None:
            _log(f"WARN: нет onnx для {behavior_stem(behavior)}")
        else:
            _log(f"policy map: {behavior_stem(behavior)} <- {pol.path.name}")
    _log(
        "reload trigger: только конец эпизода Jack "
        "(смерть Lily/George mid-frame больше не грузит onnx)"
    )

    steps = 0
    logged_first_act = set()
    act_hist = {behavior_stem(b): {} for b in behavior_names}
    # Без sleep-пейсинга: Unity и так ждёт env.step(); sleep только добавлял пустые паузы.
    # Считаем EMA времени шага — «HITCH» только если шаг заметно хуже обычного.
    ema_dt = 0.05
    slow_count = 0
    hitch_count = 0
    hitch_log_cooldown = 0.0
    window_t0 = time.perf_counter()

    while True:
        t0 = time.perf_counter()
        jack_episode_ended = False
        for behavior in behavior_names:
            decision_steps, terminal_steps = env.get_steps(behavior)
            if len(terminal_steps) > 0 and behavior in jack_keys:
                jack_episode_ended = True

            n = len(decision_steps)
            if n == 0:
                continue

            policy = bank.ensure(behavior)
            if policy is None:
                spec = env.behavior_specs[behavior]
                branches = spec.action_spec.discrete_branches
                actions = np.zeros((n, len(branches)), dtype=np.int32)
            else:
                obs_list = list(decision_steps.obs)
                masks = None
                if decision_steps.action_mask is not None:
                    masks = 1.0 - np.concatenate(
                        [m.astype(np.float32) for m in decision_steps.action_mask],
                        axis=1,
                    )
                actions = policy.act(obs_list, masks)
                if actions.shape[0] != n:
                    actions = np.resize(actions, (n, actions.shape[-1]))

            stem = behavior_stem(behavior)
            if stem not in logged_first_act:
                logged_first_act.add(stem)
                src = policy.path.name if policy is not None else "ZEROS"
                _log(f"first act {stem}: {actions[0].tolist()} from {src}")
            key = tuple(int(x) for x in actions[0].tolist())
            act_hist[stem][key] = act_hist[stem].get(key, 0) + 1

            at = ActionTuple()
            at.add_discrete(actions)
            env.set_actions(behavior, at)

        if jack_episode_ended:
            bank.request_reload()

        env.step()
        steps += 1

        dt = time.perf_counter() - t0
        ema_dt = 0.9 * ema_dt + 0.1 * dt
        if dt > max(0.12, 2.5 * ema_dt):
            hitch_count += 1
            now = time.perf_counter()
            if now >= hitch_log_cooldown:
                hitch_log_cooldown = now + 3.0
                _log(
                    f"HITCH step={steps} dt={dt*1000:.0f}ms "
                    f"(обычный шаг ≈{ema_dt*1000:.0f}ms / {1.0/max(ema_dt,1e-3):.0f} FPS)"
                )
        elif dt > 0.05:
            slow_count += 1

        if steps % step_log_every == 0:
            wall = time.perf_counter() - window_t0
            fps = step_log_every / max(wall, 1e-3)
            parts = []
            for stem, hist in act_hist.items():
                top = sorted(hist.items(), key=lambda x: -x[1])[:3]
                parts.append(f"{stem}:{top}")
                hist.clear()
            _log(
                f"steps={steps} fps≈{fps:.1f} step≈{ema_dt*1000:.0f}ms "
                f"hitches={hitch_count} slow(>{50}ms)={slow_count} "
                f"act_top {'; '.join(parts)}"
            )
            hitch_count = 0
            slow_count = 0
            window_t0 = time.perf_counter()


def parse_args():
    p = argparse.ArgumentParser(description="Unity stream + onnxruntime inference")
    p.add_argument("--run-id", required=True)
    p.add_argument("--env", required=True, help="path to .x86_64 build")
    p.add_argument("--port", type=int, default=7000)
    p.add_argument("--results-dir", default="results")
    p.add_argument(
        "--poll-sec",
        type=float,
        default=0.0,
        help="0 = только при конце эпизода Jack; >0 = ещё страховочный интервал",
    )
    p.add_argument("--timeout", type=int, default=300)
    p.add_argument("--time-scale", type=float, default=1.0)
    p.add_argument("--target-fps", type=float, default=30.0)
    p.add_argument("--quality-level", type=int, default=1)
    p.add_argument("--width", type=int, default=1920)
    p.add_argument("--height", type=int, default=1080)
    return p.parse_args()


def main() -> int:
    args = parse_args()
    run_dir = Path(args.results_dir) / args.run_id
    env_path = Path(args.env)
    if not env_path.is_file():
        _log(f"ERROR: build not found: {env_path}")
        return 1

    try:
        import onnxruntime  # noqa: F401
        from mlagents_envs.environment import UnityEnvironment
        from mlagents_envs.side_channel.engine_configuration_channel import (
            EngineConfigurationChannel,
        )
    except ImportError as e:
        _log(f"ERROR: нужен conda env mlagents ({e})")
        _log("  conda activate mlagents && pip install onnxruntime")
        return 1

    bank = PolicyBank(run_dir, poll_sec=args.poll_sec)
    if not bank.ready():
        _log(f"жду первые .onnx в {run_dir}/<Behavior>/ ...")
        while not bank.ready():
            bank._reload_all_blocking()
            if bank.ready():
                break
            time.sleep(2.0)
        _log("onnx найдены, стартую Unity")
    _log(
        "weight reload: on Jack episode end"
        + (f" + every {args.poll_sec:.0f}s" if args.poll_sec > 0 else "")
    )

    engine = EngineConfigurationChannel()
    additional = [
        "-forestStreamOnly",
        "-forestExternalBrain",
        "-forestResultsDir",
        str(run_dir),
    ]
    _log(
        f"start Unity {env_path} port={args.port} DISPLAY={os.environ.get('DISPLAY')} "
        f"fps={args.target_fps} quality={args.quality_level} "
        f"{args.width}x{args.height}"
    )
    env = UnityEnvironment(
        file_name=str(env_path.resolve()),
        base_port=args.port,
        no_graphics=False,
        timeout_wait=args.timeout,
        side_channels=[engine],
        additional_args=additional,
    )
    if hasattr(engine, "set_configuration_parameters"):
        engine.set_configuration_parameters(
            width=args.width,
            height=args.height,
            time_scale=args.time_scale,
            target_frame_rate=int(args.target_fps) if args.target_fps > 0 else -1,
            quality_level=args.quality_level,
        )
    elif hasattr(engine, "set_time_scale"):
        engine.set_time_scale(args.time_scale)
    else:
        _log("WARN: не удалось выставить engine config")
    env.reset()

    try:
        try:
            os.nice(-5)
        except OSError:
            pass
        run_loop(env, bank, target_fps=0.0)  # без sleep-пейсинга
    except KeyboardInterrupt:
        _log("stop")
    finally:
        bank.close()
        env.close()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
