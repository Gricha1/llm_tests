#!/usr/bin/env bash
# AEnt MATH on 2x A100 80GB — hyperparameters from examples/grpo_trainer/run_aent_q1b5_math.sh
# (paper README + official MATH recipe), not from run_aent_math_titan.sh (Titan smoke).
#
# Requires in image: flash-attn (Dockerfile.a100) for use_remove_padding + clamp_entropy.
# Override: MODEL_PATH, NUM_GPU, ROLLOUT_NAME=vllm (when vLLM image), ...
set -euo pipefail
if [[ "${AENT_TRACE_SHELL:-0}" == "1" ]]; then
  set -x
fi
export HYDRA_FULL_ERROR=1
export PYTHONUNBUFFERED=1
export VERL_ATTN_IMPLEMENTATION="${VERL_ATTN_IMPLEMENTATION:-flash_attention_2}"
export PYTORCH_CUDA_ALLOC_CONF="${PYTORCH_CUDA_ALLOC_CONF:-expandable_segments:True}"
# Lower peak VRAM for clamp_entropy softmax chunks (see verl/utils/torch_functional.py).
export VERL_ENTROPY_CHUNK_SIZE="${VERL_ENTROPY_CHUNK_SIZE:-32}"

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "${REPO_ROOT}"
export TENSORBOARD_DIR="${TENSORBOARD_DIR:-${REPO_ROOT}/logs/tfevent}"

# Comet ML — workspace gregory-gorbov, project llm_test.
# Override via env or ${REPO_ROOT}/.comet.env (takes precedence when sourced below).
COMET_ENV_FILE="${COMET_ENV_FILE:-${REPO_ROOT}/.comet.env}"
if [[ -f "${COMET_ENV_FILE}" ]]; then
  set -a
  # shellcheck disable=SC1090
  source "${COMET_ENV_FILE}"
  set +a
fi
export COMET_WORKSPACE="${COMET_WORKSPACE:-gregory-gorbov}"
export COMET_API_KEY="${COMET_API_KEY:-3OfuYHwcRgIwG7DzgzJ190igY}"

if [[ -f /.dockerenv ]]; then
  export AENT_IN_DOCKER=1
  unset NVIDIA_VISIBLE_DEVICES
  if [[ "${AENT_KEEP_CUDA_VISIBLE_DEVICES:-0}" != "1" ]]; then
    unset CUDA_VISIBLE_DEVICES
  fi
fi

# --- MATH recipe (run_aent_q1b5_math.sh); only NUM_GPU / MODEL_PATH adapted for this host ---
MODEL_PATH="${MODEL_PATH:-Qwen/Qwen2.5-Math-1.5B}"
NUM_GPU="${NUM_GPU:-2}"
# Official recipe: vllm on 4 GPUs. Auto-pick vllm when installed; else hf (needs micro-batch chunking).
if [[ -z "${ROLLOUT_NAME:-}" ]]; then
  if python3 -c "import vllm" 2>/dev/null; then
    ROLLOUT_NAME=vllm
  else
    ROLLOUT_NAME=hf
  fi
fi
TRAIN_BATCH_SIZE="${TRAIN_BATCH_SIZE:-512}"
PPO_MINI_BATCH="${PPO_MINI_BATCH:-128}"
PPO_MICRO_BATCH="${PPO_MICRO_BATCH:-16}"
ROLLOUT_N="${ROLLOUT_N:-16}"
# 2x80GB colocated FSDP+vLLM needs a lower KV budget than 4-GPU ml3 (official uses 0.55 on 4 GPUs).
VLLM_GPU_MEM="${VLLM_GPU_MEMORY_UTILIZATION:-0.40}"
# HF rollout: without micro_batch_size, hf_rollout generates the full batch at once → OOM at max_response=3072.
HF_ROLLOUT_MICRO_BATCH="${HF_ROLLOUT_MICRO_BATCH:-1}"
GEN_BATCH_SIZE="${GEN_BATCH_SIZE:-32}"
MAX_PROMPT_LENGTH="${MAX_PROMPT_LENGTH:-1024}"
MAX_RESPONSE_LENGTH="${MAX_RESPONSE_LENGTH:-3072}"
TOTAL_EPOCHS="${TOTAL_EPOCHS:-30}"
SAVE_FREQ="${SAVE_FREQ:-45}"
TEST_FREQ="${TEST_FREQ:-15}"
USE_REMOVE_PADDING="${USE_REMOVE_PADDING:-True}"
CLAMP_ENTROPY="${CLAMP_ENTROPY:-True}"
ROLLOUT_LOG_PROB_MB="${ROLLOUT_LOG_PROB_MB:-32}"
REF_LOG_PROB_MB="${REF_LOG_PROB_MB:-32}"

PROJECT_NAME="${COMET_PROJECT:-llm_test}"
EXPERIMENT_NAME="${EXPERIMENT_NAME:-aent MATH A100}"

TRAIN_PARQUET="${TRAIN_PARQUET:-datasets/processed_math/train.parquet}"
if [[ ! -f "${TRAIN_PARQUET}" ]]; then
  echo "Missing ${TRAIN_PARQUET}; preparing from HuggingFace (DigitalLearningGmbH/MATH-lighteval)..."
  python3 docker/prepare_math_dataset.py \
    --out_dir datasets/processed_math \
    --train_n "${PREPARE_TRAIN_N:-512}" \
    --test_n "${PREPARE_TEST_N:-128}"
fi

# Val benches from run_aent_q1b5_math.sh — use only files that exist (prepare via examples/data_preprocess/ on ml3).
if [[ -z "${VAL_FILES:-}" ]]; then
  VAL_OFFICIAL=(
    datasets/processed_MATH-Hard/test.parquet
    datasets/processed_MATH-500/test.parquet
    datasets/processed_aime24/test.parquet
    datasets/processed_amc23/test.parquet
    datasets/processed_minerva/test.parquet
    datasets/processed_olympiadbench/test.parquet
  )
  VAL_EXISTING=()
  for f in "${VAL_OFFICIAL[@]}"; do
    if [[ -f "${f}" ]]; then
      VAL_EXISTING+=("${f}")
    fi
  done
  if [[ ${#VAL_EXISTING[@]} -eq 0 ]]; then
    if [[ ! -f datasets/processed_math/test.parquet ]]; then
      echo "ERROR: no val parquet found; run docker/prepare_math_dataset.py first."
      exit 1
    fi
    VAL_EXISTING=(datasets/processed_math/test.parquet)
    echo "NOTE: using HF sample val (datasets/processed_math/test.parquet)."
    echo "      For full recipe val, preprocess 6 benches with examples/data_preprocess/ (recipe/aent/README.md)."
  elif [[ ${#VAL_EXISTING[@]} -lt 6 ]]; then
    echo "NOTE: using ${#VAL_EXISTING[@]}/6 val benches: ${VAL_EXISTING[*]}"
    echo "      Preprocess missing benches with examples/data_preprocess/."
  fi
  VAL_FILES="["
  for i in "${!VAL_EXISTING[@]}"; do
    [[ "${i}" -gt 0 ]] && VAL_FILES+=","
    VAL_FILES+="'${VAL_EXISTING[$i]}'"
  done
  VAL_FILES+="]"
fi

EXTRA_ARGS=()
if [[ "${ROLLOUT_NAME}" == "hf" ]]; then
  echo "WARN: rollout.name=hf — chunking gen (micro_batch=${HF_ROLLOUT_MICRO_BATCH}, gen_batch=${GEN_BATCH_SIZE}). Rebuild image for vLLM (official recipe)."
  EXTRA_ARGS+=(
    "data.gen_batch_size=${GEN_BATCH_SIZE}"
    "actor_rollout_ref.rollout.micro_batch_size=${HF_ROLLOUT_MICRO_BATCH}"
    actor_rollout_ref.rollout.dtype=float16
    actor_rollout_ref.actor.ppo_micro_batch_size_per_gpu=8
    actor_rollout_ref.rollout.log_prob_micro_batch_size_per_gpu=8
    actor_rollout_ref.ref.log_prob_micro_batch_size_per_gpu=8
    actor_rollout_ref.actor.entropy_checkpointing=True
    actor_rollout_ref.ref.entropy_checkpointing=True
    actor_rollout_ref.actor.fsdp_config.optimizer_offload=True
    actor_rollout_ref.actor.use_torch_compile=False
  )
elif [[ "${ROLLOUT_NAME}" == "vllm" ]]; then
  EXTRA_ARGS+=(
    "actor_rollout_ref.rollout.gpu_memory_utilization=${VLLM_GPU_MEM}"
    actor_rollout_ref.actor.use_torch_compile=False
  )
fi

echo "=== AEnt MATH A100 (recipe: run_aent_q1b5_math) ==="
echo "  NUM_GPU=${NUM_GPU}  MODEL_PATH=${MODEL_PATH}  rollout=${ROLLOUT_NAME}"
echo "  max_prompt=${MAX_PROMPT_LENGTH}  max_response=${MAX_RESPONSE_LENGTH}  clamp_entropy=${CLAMP_ENTROPY}  use_remove_padding=${USE_REMOVE_PADDING}"
if [[ "${ROLLOUT_NAME}" == "vllm" ]]; then
  echo "  vllm gpu_memory_utilization=${VLLM_GPU_MEM} (raise VLLM_GPU_MEMORY_UTILIZATION if KV cache is too small)"
fi

exec python3 -u -m recipe.aent.main_aent \
    algorithm.adv_estimator=grpo \
    data.train_files="${TRAIN_PARQUET}" \
    data.val_files="${VAL_FILES}" \
    data.train_batch_size="${TRAIN_BATCH_SIZE}" \
    data.max_prompt_length="${MAX_PROMPT_LENGTH}" \
    data.max_response_length="${MAX_RESPONSE_LENGTH}" \
    data.filter_overlong_prompts=True \
    data.truncation=error \
    actor_rollout_ref.ref.strategy=fsdp2 \
    actor_rollout_ref.actor.strategy=fsdp2 \
    actor_rollout_ref.model.path="${MODEL_PATH}" \
    actor_rollout_ref.actor.optim.lr=2e-6 \
    actor_rollout_ref.model.use_remove_padding="${USE_REMOVE_PADDING}" \
    actor_rollout_ref.actor.ppo_mini_batch_size="${PPO_MINI_BATCH}" \
    actor_rollout_ref.actor.ppo_micro_batch_size_per_gpu="${PPO_MICRO_BATCH}" \
    actor_rollout_ref.actor.use_kl_loss=False \
    actor_rollout_ref.actor.kl_loss_coef=0 \
    actor_rollout_ref.actor.kl_loss_type=low_var_kl \
    actor_rollout_ref.actor.entropy_coeff=0.002 \
    actor_rollout_ref.actor.clamp_entropy="${CLAMP_ENTROPY}" \
    actor_rollout_ref.actor.clamp_p=0.33 \
    actor_rollout_ref.actor.adaptive_entropy.entropy_low=0.15 \
    actor_rollout_ref.actor.adaptive_entropy.entropy_high=0.24 \
    actor_rollout_ref.actor.adaptive_entropy.entropy_coeff_lr=0.002 \
    actor_rollout_ref.actor.adaptive_entropy.entropy_coeff_reg=0 \
    actor_rollout_ref.actor.adaptive_entropy.entropy_coeff_warmup=30 \
    actor_rollout_ref.actor.adaptive_entropy.entropy_coeff_clip_high=0.009 \
    actor_rollout_ref.actor.adaptive_entropy.entropy_coeff_clip_low=0.0006 \
    actor_rollout_ref.actor.ppo_epochs=1 \
    actor_rollout_ref.model.enable_gradient_checkpointing=True \
    actor_rollout_ref.actor.fsdp_config.param_offload=False \
    actor_rollout_ref.actor.fsdp_config.optimizer_offload=False \
    actor_rollout_ref.rollout.log_prob_micro_batch_size_per_gpu="${ROLLOUT_LOG_PROB_MB}" \
    actor_rollout_ref.rollout.tensor_model_parallel_size="${NUM_GPU}" \
    actor_rollout_ref.rollout.name="${ROLLOUT_NAME}" \
    actor_rollout_ref.rollout.n="${ROLLOUT_N}" \
    actor_rollout_ref.rollout.val_kwargs.do_sample=True \
    actor_rollout_ref.rollout.val_kwargs.top_p=0.95 \
    actor_rollout_ref.rollout.val_kwargs.top_k=20 \
    actor_rollout_ref.rollout.val_kwargs.temperature=0.6 \
    actor_rollout_ref.rollout.val_kwargs.n=4 \
    actor_rollout_ref.ref.log_prob_micro_batch_size_per_gpu="${REF_LOG_PROB_MB}" \
    actor_rollout_ref.ref.fsdp_config.param_offload=True \
    algorithm.kl_ctrl.kl_coef=0 \
    trainer.critic_warmup=0 \
    trainer.logger=['console','tensorboard','comet_ml'] \
    "trainer.project_name=${PROJECT_NAME}" \
    "trainer.experiment_name=${EXPERIMENT_NAME}" \
    trainer.n_gpus_per_node="${NUM_GPU}" \
    trainer.nnodes=1 \
    trainer.save_freq="${SAVE_FREQ}" \
    trainer.test_freq="${TEST_FREQ}" \
    trainer.total_epochs="${TOTAL_EPOCHS}" \
    "${EXTRA_ARGS[@]}" \
    "$@"
