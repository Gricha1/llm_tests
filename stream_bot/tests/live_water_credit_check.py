#!/usr/bin/env python3
import json, re, subprocess, time

REMOTE = "lab_comp"
BOT = "http://127.0.0.1:8765"
LOG = "~/lab_work_space/forest_survival/results/streaming_survival.log"


def ssh(cmd, timeout=90):
    r = subprocess.run(
        ["ssh.exe", REMOTE, cmd],
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        timeout=timeout,
    )
    return r.stdout or ""


def curl(body):
    payload = json.dumps(body, ensure_ascii=False).replace("'", "'\\''")
    out = ssh(
        f"curl -s -m 30 -X POST -H 'Content-Type: application/json' "
        f"-d '{payload}' {BOT}/local_chat"
    )
    try:
        return json.loads(out) if out.strip() else {}
    except json.JSONDecodeError:
        return {"_raw": out[:200]}


def main():
    u = "wfix1"
    curl({"username": u, "message": "#exit"})
    time.sleep(0.3)
    curl({"username": u, "message": "#join"})
    time.sleep(0.5)
    out = curl({"username": u, "message": "#do добудь воду"})
    acts = [
        c.get("action")
        for c in (out.get("unity_commands") or [])
        if isinstance(c, dict) and c.get("type") == "streaming_survival_action"
    ]
    print("assign", acts)
    line0 = int(ssh(f"wc -l < {LOG}").strip().split()[0])
    print("mark", line0)
    ok = False
    for i in range(20):
        time.sleep(5)
        log = ssh(
            f"tail -n +{line0} {LOG} 2>/dev/null | grep -E 'wfix1|SSRes' | tail -n 60 || true"
        )
        cred = re.findall(
            r"water_credited user=wfix1 pos=\((-?[\d.]+),(-?[\d.]+)\)", log
        )
        nocred = re.findall(
            r"water_no_credit user=wfix1 pos=\((-?[\d.]+),(-?[\d.]+)\)", log
        )
        gather = re.findall(
            r"water_gather user=wfix1 pos=\((-?[\d.]+),(-?[\d.]+)\)", log
        )
        route = re.findall(
            r"water_route user=wfix1 n=(\d+) last=\((-?[\d.]+),(-?[\d.]+)\)", log
        )
        res = re.findall(r"\[SSRes\].*?water=(\d+)", log)
        print(
            f"[{i}] water={res[-1] if res else None} "
            f"cred={cred[-1] if cred else None} "
            f"gather={gather[-1] if gather else None} "
            f"route={route[-1] if route else None} "
            f"nocred={len(nocred)}"
        )
        if cred:
            x, z = map(float, cred[-1])
            near_spawn = abs(x - 10.2) < 5 and abs(z - 13.3) < 5
            near_house = abs(x + 3.3) < 5 and abs(z - 18.9) < 5
            ok = z >= 20.0 and not near_spawn and not near_house
            print("PASS" if ok else "FAIL", f"credit at ({x:.1f},{z:.1f})")
            break
        # early fail: water increased while still near spawn
        if res and int(res[-1]) > 0 and gather:
            gx, gz = map(float, gather[-1])
            if gz < 18:
                print("FAIL water gather too close to spawn", gather[-1])
                ok = False
                break
    else:
        print("FAIL no water_credited in time")
        ok = False
    curl({"username": u, "message": "#exit"})
    return 0 if ok else 1


if __name__ == "__main__":
    raise SystemExit(main())
