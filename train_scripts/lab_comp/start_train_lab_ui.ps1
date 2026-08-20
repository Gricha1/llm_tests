# Start Train Lab UI: prefer lab_comp via SSH, else local Windows.
# Usage:  powershell -File train_scripts/lab_comp/start_train_lab_ui.ps1
# URL:    http://127.0.0.1:8877

$ErrorActionPreference = "Continue"
$Root = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
Set-Location $Root

$Port = if ($env:FOREST_UI_PORT) { $env:FOREST_UI_PORT } else { "8877" }
$SshHost = "lab_comp"
$hostFile = Join-Path $Root ".train_lab_ui_ssh_host"
if (Test-Path $hostFile) {
    $saved = (Get-Content $hostFile -Raw).Trim()
    if ($saved -and $saved -ne "local") { $SshHost = $saved }
}

function Test-LabSsh {
    param([string]$HostName)
    $out = & ssh.exe -o BatchMode=yes -o ConnectTimeout=8 -o ServerAliveInterval=5 $HostName "echo LAB_OK" 2>$null
    return ($LASTEXITCODE -eq 0 -and ("$out" -match "LAB_OK"))
}

function Stop-LocalPort {
    param([int]$P)
    try {
        $conns = Get-NetTCPConnection -LocalPort $P -State Listen -ErrorAction SilentlyContinue
        foreach ($c in $conns) {
            if ($c.OwningProcess) {
                Write-Host "[ui] free local :$P (pid $($c.OwningProcess))"
                Stop-Process -Id $c.OwningProcess -Force -ErrorAction SilentlyContinue
            }
        }
    } catch { }
}

function Write-UnixFile {
    param([string]$Path, [string]$Content)
    $utf8 = New-Object System.Text.UTF8Encoding $false
    [System.IO.File]::WriteAllText($Path, ($Content -replace "`r`n", "`n" -replace "`r", "`n"), $utf8)
}

function Start-UiOnLab {
    param([string]$HostName)
    Write-Host "[ui] SSH ok -> sync + start UI on lab ($HostName)"
    & scp.exe -o BatchMode=yes `
        (Join-Path $Root "train_scripts\lab_comp\train_lab_ui.py") `
        (Join-Path $Root "train_scripts\lab_comp\stream_onnx_infer.py") `
        "${HostName}:~/lab_work_space/forest_survival/train_scripts/lab_comp/"
    if ($LASTEXITCODE -ne 0) { throw "scp train_lab_ui.py failed" }

    $remotePath = Join-Path $Root ".train_lab_ui\_remote_start_ui.sh"
    New-Item -ItemType Directory -Force -Path (Split-Path $remotePath) | Out-Null
    $remote = @"
#!/usr/bin/env bash
set -e
PROJ="`$HOME/lab_work_space/forest_survival"
cd "`$PROJ"
mkdir -p results
for pid in `$(pgrep -f 'train_scripts/lab_comp/train_lab_ui.py' || true); do
  kill "`$pid" 2>/dev/null || true
done
fuser -k 8877/tcp 2>/dev/null || true
sleep 1
rm -f .train_lab_ui_ssh_host
export FOREST_UI_LOCAL=1
export FOREST_UI_HOST=0.0.0.0
export FOREST_UI_PORT=8877
PY="`$HOME/anaconda3/envs/mlagents/bin/python"
if [ ! -x "`$PY" ]; then PY=python3; fi
nohup env FOREST_UI_LOCAL=1 FOREST_UI_HOST=0.0.0.0 "`$PY" -u train_scripts/lab_comp/train_lab_ui.py \
  > results/train_lab_ui.log 2>&1 < /dev/null &
echo "lab_ui_pid=`$!"
sleep 2
(ss -ltnp 2>/dev/null || netstat -ltnp 2>/dev/null || true) | grep 8877 || true
curl -s -m 3 http://127.0.0.1:8877/ | head -c 120 || true
echo
tail -n 6 results/train_lab_ui.log || true
"@
    Write-UnixFile -Path $remotePath -Content $remote
    & scp.exe -o BatchMode=yes $remotePath "${HostName}:/tmp/start_train_lab_ui_remote.sh"
    & ssh.exe -o BatchMode=yes -o ServerAliveInterval=30 $HostName "bash /tmp/start_train_lab_ui_remote.sh"
    if ($LASTEXITCODE -ne 0) { throw "remote UI start failed" }

    Stop-LocalPort -P ([int]$Port)
    $tunnelLog = Join-Path $Root ".train_lab_ui\ssh_tunnel_8877.log"
    Get-CimInstance Win32_Process -Filter "Name='ssh.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -match "8877:127\.0\.0\.1:8877" } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Start-Process -FilePath "ssh.exe" -ArgumentList @(
        "-o", "BatchMode=yes",
        "-o", "ServerAliveInterval=30",
        "-o", "ExitOnForwardFailure=yes",
        "-N", "-L", "${Port}:127.0.0.1:8877",
        $HostName
    ) -WindowStyle Hidden -RedirectStandardError $tunnelLog -RedirectStandardOutput (Join-Path $Root ".train_lab_ui\ssh_tunnel_8877.out")
    Start-Sleep -Seconds 2
    Write-Host "[ui] mode=LAB url=http://127.0.0.1:$Port (tunnel -> $HostName)"
}

function Start-UiLocal {
    Write-Host "[ui] starting Train Lab UI locally"
    Stop-LocalPort -P ([int]$Port)
    $pyCmd = Get-Command python -ErrorAction SilentlyContinue
    if (-not $pyCmd) { $pyCmd = Get-Command python3 -ErrorAction SilentlyContinue }
    if (-not $pyCmd) { throw "python not found on PATH" }
    $py = $pyCmd.Source
    $logDir = Join-Path $Root ".train_lab_ui\logs"
    New-Item -ItemType Directory -Force -Path $logDir | Out-Null
    $outLog = Join-Path $logDir "train_lab_ui_local.out.log"
    $errLog = Join-Path $logDir "train_lab_ui_local.err.log"
    Remove-Item Env:FOREST_UI_LOCAL -ErrorAction SilentlyContinue
    $env:FOREST_UI_HOST = "127.0.0.1"
    $env:FOREST_UI_PORT = "$Port"
    Start-Process -FilePath $py -ArgumentList @(
        "-u", "train_scripts\lab_comp\train_lab_ui.py"
    ) -WorkingDirectory $Root -WindowStyle Hidden `
      -RedirectStandardOutput $outLog -RedirectStandardError $errLog
    Start-Sleep -Seconds 3
    Write-Host "[ui] mode=LOCAL url=http://127.0.0.1:$Port"
    Write-Host "[ui] log=$outLog"
}

$ok = Test-LabSsh -HostName $SshHost
if (-not $ok -and $SshHost -ne "lab_comp") {
    Write-Host "[ui] $SshHost unavailable, try lab_comp"
    $SshHost = "lab_comp"
    $ok = Test-LabSsh -HostName $SshHost
}

if ($ok) {
    try {
        Start-UiOnLab -HostName $SshHost
    } catch {
        Write-Host "[ui] lab start failed: $_ -> fallback local"
        Start-UiLocal
    }
} else {
    Write-Host "[ui] SSH unavailable -> local"
    Start-UiLocal
}

Start-Sleep -Seconds 1
try {
    $r = Invoke-WebRequest -Uri "http://127.0.0.1:$Port/" -UseBasicParsing -TimeoutSec 8
    Write-Host "[ui] HTTP $($r.StatusCode) OK"
} catch {
    Write-Host "[ui] WARN: http://127.0.0.1:$Port not ready: $_"
}
try { Start-Process "http://127.0.0.1:$Port/" } catch { }
Write-Host "[ui] done"
