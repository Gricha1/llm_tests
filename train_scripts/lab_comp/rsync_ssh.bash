#!/usr/bin/env bash
# Общий SSH/rsync transport для lab_comp из WSL.
# ssh.exe lab_comp (Windows config) часто работает, когда WSL ssh -i key → timeout.
#
#   source train_scripts/lab_comp/rsync_ssh.bash
#   lab_comp_init_ssh
#   rsync ... -e "${LAB_COMP_RSYNC_SSH}" src/ "${LAB_COMP_RSYNC_REMOTE}:${REMOTE_DIR}/"

lab_comp_init_ssh() {
  REMOTE_DIR="${REMOTE_DIR:-~/lab_work_space/forest_survival}"
  SSH_KEY="${SSH_KEY:-${HOME}/.ssh/lab_comp_key}"

  mkdir -p "${HOME}/.ssh"
  if [ ! -f "${SSH_KEY}" ] && [ -f "/mnt/c/Users/User/.ssh/id_ed25519" ]; then
    cp "/mnt/c/Users/User/.ssh/id_ed25519" "${SSH_KEY}" 2>/dev/null || true
    chmod 600 "${SSH_KEY}" 2>/dev/null || true
  fi

  LAB_COMP_RSYNC_SSH=""
  LAB_COMP_RSYNC_REMOTE=""
  LAB_COMP_SSH_CMD=()

  # Windows ssh.exe видит ZeroTier; WSL ssh часто нет → сначала только ssh.exe.
  # Порядок: alias → ZeroTier cds_team → ZeroTier home → LAN.
  if command -v ssh.exe >/dev/null 2>&1; then
    local candidate
    for candidate in \
      "lab_comp" \
      "reedgern@10.43.71.7" \
      "reedgern@192.168.194.7" \
      "lab_comp_local" \
      "reedgern@192.168.50.18"
    do
      if ssh.exe -o BatchMode=yes -o ConnectTimeout=8 "${candidate}" true 2>/dev/null; then
        LAB_COMP_RSYNC_SSH="ssh.exe"
        LAB_COMP_RSYNC_REMOTE="${candidate}"
        LAB_COMP_SSH_CMD=(ssh.exe)
        echo "[lab_comp_ssh] transport=ssh.exe host=${candidate}"
        return 0
      fi
    done
  fi

  local fallback="${REMOTE:-reedgern@10.43.71.7}"
  LAB_COMP_RSYNC_SSH="ssh -i ${SSH_KEY} -o StrictHostKeyChecking=no -o ConnectTimeout=8"
  LAB_COMP_RSYNC_REMOTE="${fallback}"
  LAB_COMP_SSH_CMD=(ssh -i "${SSH_KEY}" -o StrictHostKeyChecking=no -o ConnectTimeout=8)
  echo "[lab_comp_ssh] transport=wsl-ssh host=${fallback}"

  if ! "${LAB_COMP_SSH_CMD[@]}" "${fallback}" true 2>/dev/null; then
    echo "ERROR: не удалось подключиться к lab_comp." >&2
    echo "  Проверь ZeroTier (cds_team 10.43.71.* или network_home 192.168.194.*):" >&2
    echo "    ssh.exe lab_comp echo ok" >&2
    echo "    ssh.exe reedgern@10.43.71.7 echo ok" >&2
    echo "    ssh.exe reedgern@192.168.194.7 echo ok" >&2
    return 1
  fi
}

lab_comp_ssh_mkdir() {
  "${LAB_COMP_SSH_CMD[@]}" "${LAB_COMP_RSYNC_REMOTE}" "mkdir -p $1"
}
