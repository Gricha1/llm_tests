#!/usr/bin/env bash
# Alias for A100 training (ml3 uses run_aent_math_titan.sh).
exec "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/run_aent_math_a100.sh" "$@"
