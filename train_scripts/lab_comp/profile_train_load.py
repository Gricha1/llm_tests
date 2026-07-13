#!/usr/bin/env python3
"""Профилирование CPU/GPU на lab_comp (или локально) за N секунд + график и выводы."""

import argparse
import csv
import os
import subprocess
import sys
from collections import namedtuple
from datetime import datetime
from pathlib import Path
from typing import List, Sequence

if sys.version_info < (3, 7):
    sys.stderr.write(
        "ERROR: нужен Python 3.7+ (сейчас {}.{}.{})\n".format(*sys.version_info[:3])
    )
    sys.exit(1)

REMOTE_COLLECTOR_BASH = r'''#!/usr/bin/env bash
set -eu
duration="${1:?}"
interval="${2:?}"

cpu_total_pct() {
  top -bn2 -d0.3 2>/dev/null | grep -E "^%?Cpu" | tail -1 | awk -F',' '{
    for (i=1;i<=NF;i++) {
      if ($i ~ /id/) { gsub(/[^0-9.]/,"",$i); idle=$i }
    }
    if (idle=="") idle=0
    printf "%.1f", 100-idle
  }'
}

proc_cpu_sum() {
  ps aux 2>/dev/null | grep -E "$1" | grep -v grep | awk '{sum+=$3} END {printf "%.1f", sum+0}'
}

gpu_line() {
  if command -v nvidia-smi >/dev/null 2>&1; then
    nvidia-smi --query-gpu=utilization.gpu,memory.used,memory.total --format=csv,noheader,nounits 2>/dev/null | head -1
  else
    echo "0,0,0"
  fi
}

echo "ts,cpu_total,gpu_util,gpu_mem_used_mb,gpu_mem_total_mb,mlagents_cpu,unity_cpu,obs_cpu,anydesk_cpu"
for ((i=0; i<duration; i++)); do
  IFS=',' read -r gpu_util gpu_used gpu_total <<< "$(gpu_line | tr -d ' ')"
  ts="$(date +%s.%N)"
  cpu="$(cpu_total_pct)"
  mlagents="$(proc_cpu_sum 'mlagents-learn')"
  unity="$(proc_cpu_sum 'stream_forest_survival')"
  obs="$(proc_cpu_sum 'obs')"
  anydesk="$(proc_cpu_sum 'anydesk')"
  echo "${ts},${cpu},${gpu_util},${gpu_used},${gpu_total},${mlagents},${unity},${obs},${anydesk}"
  if (( i + 1 < duration )); then
    sleep "${interval}"
  fi
done
'''


def run_bash_collector(duration, interval, remote_cmd=None):
    cmd = remote_cmd or ["bash", "-s", "--", str(duration), str(interval)]
    proc = subprocess.run(
        cmd,
        input=REMOTE_COLLECTOR_BASH,
        capture_output=True,
        text=True,
    )
    if proc.returncode != 0:
        if proc.stderr.strip():
            sys.stderr.write(proc.stderr.strip() + "\n")
        if proc.stdout.strip():
            sys.stderr.write(proc.stdout.strip() + "\n")
        proc.check_returncode()
    return parse_csv_lines(proc.stdout.splitlines())


Sample = namedtuple(
    "Sample",
    [
        "ts",
        "cpu_total",
        "gpu_util",
        "gpu_mem_used_mb",
        "gpu_mem_total_mb",
        "mlagents_cpu",
        "unity_cpu",
        "obs_cpu",
        "anydesk_cpu",
    ],
)


def parse_args():
    p = argparse.ArgumentParser(description="CPU/GPU profile for forest_survival train/stream")
    p.add_argument("--duration", type=int, default=20, help="seconds")
    p.add_argument("--interval", type=float, default=1.0, help="seconds between samples")
    p.add_argument("--local", action="store_true", help="run on this machine (lab_comp shell)")
    p.add_argument("--remote", action="store_true", help="ssh to lab_comp (default from WSL)")
    p.add_argument("--host", default=os.environ.get("REMOTE", "reedgern@192.168.194.7"))
    p.add_argument("--key", default=os.environ.get("SSH_KEY", os.path.expanduser("~/.ssh/lab_comp_key")))
    p.add_argument("--out-dir", default="results/profiling")
    return p.parse_args()


def collect_local(duration, interval):
    return run_bash_collector(duration, interval)


def collect_remote(host, key, duration, interval):
    ssh = [
        "ssh", "-i", key, "-o", "StrictHostKeyChecking=no", host,
        "bash", "-s", "--", str(duration), str(interval),
    ]
    try:
        return run_bash_collector(duration, interval, remote_cmd=ssh)
    except subprocess.CalledProcessError as exc:
        if exc.returncode == 255:
            sys.stderr.write(
                "ERROR: SSH не удался. Запусти на сервере: "
                "bash train_scripts/lab_comp/profile_train_load.bash --local\n"
            )
        raise


def parse_csv_lines(lines):
    rows = [line for line in lines if line.strip() and not line.startswith("ts,")]
    if not rows:
        raise RuntimeError("collector returned no data")
    samples = []
    for line in rows:
        parts = line.split(",")
        if len(parts) < 9:
            continue
        samples.append(
            Sample(
                float(parts[0]),
                float(parts[1]),
                float(parts[2]),
                float(parts[3]),
                float(parts[4]),
                float(parts[5]),
                float(parts[6]),
                float(parts[7]),
                float(parts[8]),
            )
        )
    if not samples:
        raise RuntimeError("no valid samples parsed")
    return samples


def avg(values):
    return sum(values) / len(values) if values else 0.0


def save_csv(samples, path):
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", newline="", encoding="utf-8") as f:
        w = csv.writer(f)
        w.writerow(
            [
                "ts",
                "cpu_total",
                "gpu_util",
                "gpu_mem_used_mb",
                "gpu_mem_total_mb",
                "mlagents_cpu",
                "unity_cpu",
                "obs_cpu",
                "anydesk_cpu",
            ]
        )
        for s in samples:
            w.writerow(
                [
                    "{:.3f}".format(s.ts),
                    "{:.1f}".format(s.cpu_total),
                    "{:.1f}".format(s.gpu_util),
                    "{:.1f}".format(s.gpu_mem_used_mb),
                    "{:.1f}".format(s.gpu_mem_total_mb),
                    "{:.1f}".format(s.mlagents_cpu),
                    "{:.1f}".format(s.unity_cpu),
                    "{:.1f}".format(s.obs_cpu),
                    "{:.1f}".format(s.anydesk_cpu),
                ]
            )


def plot_samples_svg(samples, svg_path):
    width, height = 1100, 780
    t0 = samples[0].ts
    duration = max(1.0, samples[-1].ts - t0)

    panels = [
        (
            "CPU %",
            [
                ("CPU total", [s.cpu_total for s in samples], "#222222", 2.5),
                ("mlagents", [s.mlagents_cpu for s in samples], "#d62728", 1.8),
                ("Unity", [s.unity_cpu for s in samples], "#2ca02c", 1.8),
                ("OBS", [s.obs_cpu for s in samples], "#9467bd", 1.8),
                ("AnyDesk", [s.anydesk_cpu for s in samples], "#ff7f0e", 1.8),
            ],
            0.0,
            max(105.0, max(s.cpu_total for s in samples) + 5.0),
        ),
        (
            "GPU %",
            [("GPU util", [s.gpu_util for s in samples], "#1f77b4", 2.5)],
            0.0,
            100.0,
        ),
        (
            "VRAM MB",
            [("VRAM used", [s.gpu_mem_used_mb for s in samples], "#17becf", 2.5)],
            0.0,
            max(1.0, max(s.gpu_mem_used_mb for s in samples) * 1.1),
        ),
    ]

    lines_out = [
        '<?xml version="1.0" encoding="UTF-8"?>',
        '<svg xmlns="http://www.w3.org/2000/svg" width="{}" height="{}">'.format(width, height),
        '<rect width="100%" height="100%" fill="#fafafa"/>',
        '<text x="40" y="28" font-size="18" font-family="Arial,sans-serif">forest_survival train/stream load</text>',
    ]

    panel_h = 230
    left, right = 60, width - 60
    plot_w = right - left

    for pi, (title, series, ymin, ymax) in enumerate(panels):
        top = 45 + pi * panel_h
        bottom = top + panel_h - 35
        plot_h = bottom - top - 20
        if ymax <= ymin:
            ymax = ymin + 1.0

        lines_out.append(
            '<rect x="40" y="{}" width="{}" height="{}" fill="#fff" stroke="#ddd"/>'.format(
                top, width - 80, panel_h - 10))
        lines_out.append(
            '<text x="50" y="{}" font-size="13" font-family="Arial,sans-serif">{}</text>'.format(
                top + 18, title))

        legend_x = width - 210
        legend_y = top + 18
        for label, vals, color, stroke in series:
            pts = []
            n = len(vals)
            for i, val in enumerate(vals):
                x = left + plot_w * (i / max(1, n - 1))
                y = bottom - 10 - plot_h * ((val - ymin) / (ymax - ymin))
                pts.append("{:.1f},{:.1f}".format(x, y))
            lines_out.append(
                '<polyline fill="none" stroke="{}" stroke-width="{}" points="{}"/>'.format(
                    color, stroke, " ".join(pts)))
            lines_out.append(
                '<text x="{}" y="{}" font-size="11" font-family="Arial,sans-serif" fill="{}">■ {}</text>'.format(
                    legend_x, legend_y, color, label))
            legend_y += 14

    lines_out.append(
        '<text x="50" y="{}" font-size="11" font-family="Arial,sans-serif">0 – {:.0f} s</text>'.format(
            height - 15, duration))
    lines_out.append("</svg>")

    svg_path.parent.mkdir(parents=True, exist_ok=True)
    svg_path.write_text("\n".join(lines_out), encoding="utf-8")


def plot_samples(samples, out_path):
    out_path = Path(out_path)
    out_path.parent.mkdir(parents=True, exist_ok=True)

    try:
        import matplotlib.pyplot as plt
    except ImportError:
        svg_path = out_path.with_suffix(".svg")
        plot_samples_svg(samples, svg_path)
        print("[profile] matplotlib нет — сохранён SVG: {}".format(svg_path))
        return svg_path

    t0 = samples[0].ts
    xs = [s.ts - t0 for s in samples]

    fig, axes = plt.subplots(3, 1, figsize=(12, 9), sharex=True)
    fig.suptitle("forest_survival train/stream load", fontsize=13)

    axes[0].plot(xs, [s.cpu_total for s in samples], label="CPU total %", color="#222", linewidth=2)
    axes[0].plot(xs, [s.mlagents_cpu for s in samples], label="mlagents-learn", color="#d62728")
    axes[0].plot(xs, [s.unity_cpu for s in samples], label="Unity (all)", color="#2ca02c")
    axes[0].plot(xs, [s.obs_cpu for s in samples], label="OBS", color="#9467bd")
    axes[0].plot(xs, [s.anydesk_cpu for s in samples], label="AnyDesk", color="#ff7f0e")
    axes[0].set_ylabel("% CPU")
    axes[0].set_ylim(0, max(105, max(s.cpu_total for s in samples) + 5))
    axes[0].grid(True, alpha=0.3)
    axes[0].legend(loc="upper right", fontsize=8)

    axes[1].plot(xs, [s.gpu_util for s in samples], label="GPU util %", color="#1f77b4", linewidth=2)
    axes[1].set_ylabel("GPU %")
    axes[1].set_ylim(0, 100)
    axes[1].grid(True, alpha=0.3)
    axes[1].legend(loc="upper right")

    axes[2].plot(xs, [s.gpu_mem_used_mb for s in samples], label="VRAM used MB", color="#17becf", linewidth=2)
    if samples[0].gpu_mem_total_mb > 0:
        axes[2].axhline(samples[0].gpu_mem_total_mb, color="#aaa", linestyle="--", label="VRAM total")
    axes[2].set_ylabel("MB")
    axes[2].set_xlabel("seconds")
    axes[2].grid(True, alpha=0.3)
    axes[2].legend(loc="upper right")

    png_path = out_path if out_path.suffix.lower() == ".png" else out_path.with_suffix(".png")
    fig.tight_layout()
    fig.savefig(png_path, dpi=140)
    plt.close(fig)
    return png_path


def print_conclusions(samples):
    cpu_total = avg([s.cpu_total for s in samples])
    gpu_util = avg([s.gpu_util for s in samples])
    mlagents = avg([s.mlagents_cpu for s in samples])
    unity = avg([s.unity_cpu for s in samples])
    obs = avg([s.obs_cpu for s in samples])
    anydesk = avg([s.anydesk_cpu for s in samples])
    gpu_mem = avg([s.gpu_mem_used_mb for s in samples])
    gpu_mem_max = max(s.gpu_mem_used_mb for s in samples)

    peaks = {
        "cpu_total": max(s.cpu_total for s in samples),
        "gpu_util": max(s.gpu_util for s in samples),
        "mlagents": max(s.mlagents_cpu for s in samples),
        "unity": max(s.unity_cpu for s in samples),
    }

    print("\n=== Сводка (среднее за {:.0f} с, {} точек) ===".format(
        samples[-1].ts - samples[0].ts, len(samples)))
    print("  CPU total:     {:5.1f}%  (пик {:.1f}%)".format(cpu_total, peaks["cpu_total"]))
    print("  mlagents:      {:5.1f}%".format(mlagents))
    print("  Unity (все):   {:5.1f}%  (пик {:.1f}%)".format(unity, peaks["unity"]))
    print("  OBS:           {:5.1f}%".format(obs))
    print("  AnyDesk:       {:5.1f}%".format(anydesk))
    print("  GPU util:      {:5.1f}%  (пик {:.1f}%)".format(gpu_util, peaks["gpu_util"]))
    print("  VRAM:          {:.0f} MB avg, {:.0f} MB max".format(gpu_mem, gpu_mem_max))

    print("\n=== Выводы ===")
    bottlenecks = []

    if gpu_util >= 75 or peaks["gpu_util"] >= 90:
        bottlenecks.append(("GPU", "CUDA/PPO + рендер Unity конкурируют за видеокарту"))
    if mlagents >= 40 or peaks["mlagents"] >= 70:
        bottlenecks.append(("mlagents", "Python-тренер грузит CPU — presentation ждёт шаги (рывки)"))
    if unity >= 50 or peaks["unity"] >= 80:
        bottlenecks.append(("Unity", "много Unity-процессов или тяжёлый presentation worker"))
    if obs >= 8:
        bottlenecks.append(("OBS", "кодирование/захват в OBS добавляет нагрузку"))
    if anydesk >= 10:
        bottlenecks.append(("AnyDesk", "удалённый рабочий стол мешает плавной картинке"))
    if cpu_total >= 85:
        bottlenecks.append(("CPU", "процессор в целом перегружен"))

    if not bottlenecks:
        print("  Явного узкого места нет — ищи микрофризы от hot reload весов / сброса эпизода.")
    else:
        for i, (name, msg) in enumerate(sorted(bottlenecks, key=lambda x: x[0]), 1):
            print("  {}. [{}] {}".format(i, name, msg))

    print("\n=== Рекомендации (по приоритету) ===")
    recs = []
    if mlagents >= 30 or gpu_util >= 60:
        recs.append("worker 0 → InferenceOnly (стрим без train), веса hot reload из чекпоинтов")
    if unity >= 40:
        recs.append("оставить 11 headless + 1 presentation (уже через launch_forest_env.bash)")
    if obs >= 5 or anydesk >= 8:
        recs.append("OBS: захват экрана 1080p30, AnyDesk не смотреть параллельно со стримом")
    if gpu_util >= 70:
        recs.append("разнести train (GPU) и стрим, или снизить time-scale / batch")
    if not recs:
        recs.append("сгладить hot reload: по одному агенту, не во время death overlay")
    for i, r in enumerate(recs, 1):
        print("  {}. {}".format(i, r))


def pick_python():
    for cmd in ("python3.12", "python3.11", "python3.10", "python3"):
        try:
            out = subprocess.check_output([cmd, "-c", "import sys; print(sys.version_info[:3])"], text=True)
            parts = [int(x.strip()) for x in out.strip().strip("()").split(",")]
            if tuple(parts) >= (3, 7):
                return cmd
        except (subprocess.CalledProcessError, FileNotFoundError, ValueError):
            continue
    return None


def main():
    args = parse_args()
    remote = args.remote or not args.local

    stamp = datetime.now().strftime("%Y%m%d_%H%M%S")
    out_dir = Path(args.out_dir)
    csv_path = out_dir / ("profile_%s.csv" % stamp)
    png_path = out_dir / ("profile_%s.png" % stamp)

    print("[profile] duration={}s interval={}s mode={}".format(
        args.duration, args.interval, "remote" if remote else "local"))
    if remote:
        if not Path(args.key).exists():
            sys.stderr.write("ERROR: SSH key not found: {}\n".format(args.key))
            return 1
        samples = collect_remote(args.host, args.key, args.duration, args.interval)
    else:
        samples = collect_local(args.duration, args.interval)

    save_csv(samples, csv_path)
    chart_path = plot_samples(samples, png_path)
    print("[profile] CSV: {}".format(csv_path))
    print("[profile] chart: {}".format(chart_path))
    print_conclusions(samples)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
