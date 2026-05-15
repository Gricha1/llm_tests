#!/usr/bin/env bash
# Docker / 24GB-class GPUs: HF rollout + SDPA (no vLLM in titan image), optional flash-attn off.
# Defaults work inside container with 2 GPUs. Override: MODEL_PATH, NUM_GPU, or Hydra args at end.
set -euo pipefail
if [[ "${AENT_TRACE_SHELL:-0}" == "1" ]]; then
  set -x
fi
export HYDRA_FULL_ERROR=1
export PYTHONUNBUFFERED=1
export VERL_ATTN_IMPLEMENTATION="${VERL_ATTN_IMPLEMENTATION:-sdpa}"
export TENSORBOARD_DIR="${TENSORBOARD_DIR:-/workspace/aent/logs/tfevent}"
# Smaller rows per entropy softmax chunk → lower VRAM on 24GB (see verl/utils/torch_functional.py).
export VERL_ENTROPY_CHUNK_SIZE="${VERL_ENTROPY_CHUNK_SIZE:-32}"

# Comet ML — workspace gregory-gorbov, project llm_test, run name "aent MATH".
# Set the key via env or a local file (do not commit real keys). Example file line: export COMET_API_KEY="..."
COMET_ENV_FILE="${COMET_ENV_FILE:-/workspace/aent/.comet.env}"
if [[ -f "${COMET_ENV_FILE}" ]]; then
  set -a
  # shellcheck disable=SC1090
  source "${COMET_ENV_FILE}"
  set +a
fi
export COMET_WORKSPACE="${COMET_WORKSPACE:-gregory-gorbov}"
export COMET_API_KEY="${COMET_API_KEY:-3OfuYHwcRgIwG7DzgzJ190igY}"
# Optional: paste key here for ad-hoc runs (do not commit a filled-in line):
# export COMET_API_KEY="YOUR_COMET_API_KEY"

export PYTORCH_CUDA_ALLOC_CONF="${PYTORCH_CUDA_ALLOC_CONF:-expandable_segments:True}"

# Docker: host GPU indices in NVIDIA_VISIBLE_DEVICES (e.g. 3,4 from --gpus device=3,4) break CUDA in workers.
if [[ -f /.dockerenv ]]; then
  export AENT_IN_DOCKER=1
  unset NVIDIA_VISIBLE_DEVICES
  if [[ "${AENT_KEEP_CUDA_VISIBLE_DEVICES:-0}" != "1" ]]; then
    unset CUDA_VISIBLE_DEVICES
  fi
fi

NUM_GPU="${NUM_GPU:-2}"
# Hub id works in Docker; for local snapshot: MODEL_PATH=/workspace/models/... bash ...
MODEL_PATH="${MODEL_PATH:-Qwen/Qwen2.5-Math-1.5B}"
PROJECT_NAME="${COMET_PROJECT:-llm_test}"
EXPERIMENT_NAME="${EXPERIMENT_NAME:-aent MATH}"

echo "=== AEnt Titan ===  NUM_GPU=${NUM_GPU}  MODEL_PATH=${MODEL_PATH}"
if [[ -f /.dockerenv ]]; then
  echo "Docker: NVIDIA_VISIBLE_DEVICES unset; Ray will assign cuda:0,1. Check: python3 -c \"import torch; print(torch.cuda.device_count())\""
fi

exec python3 -u -m recipe.aent.main_aent \
    algorithm.adv_estimator=grpo \
    data.train_files=datasets/processed_math/train.parquet \
    data.val_files=['datasets/processed_math/test.parquet'] \
    data.train_batch_size=16 \
    data.max_prompt_length=512 \
    data.max_response_length=768 \
    data.filter_overlong_prompts=True \
    data.truncation=error \
    actor_rollout_ref.ref.strategy=fsdp2 \
    actor_rollout_ref.actor.strategy=fsdp2 \
    actor_rollout_ref.model.path="${MODEL_PATH}" \
    actor_rollout_ref.model.use_remove_padding=False \
    actor_rollout_ref.model.enable_gradient_checkpointing=True \
    actor_rollout_ref.actor.optim.lr=2e-6 \
    actor_rollout_ref.actor.ppo_mini_batch_size=16 \
    actor_rollout_ref.actor.ppo_micro_batch_size_per_gpu=1 \
    actor_rollout_ref.actor.use_dynamic_bsz=True \
    actor_rollout_ref.actor.ppo_max_token_len_per_gpu=3072 \
    actor_rollout_ref.actor.use_torch_compile=False \
    actor_rollout_ref.actor.entropy_checkpointing=True \
    actor_rollout_ref.actor.use_kl_loss=False \
    actor_rollout_ref.actor.kl_loss_coef=0 \
    actor_rollout_ref.actor.entropy_coeff=0.002 \
    actor_rollout_ref.actor.clamp_entropy=False \
    actor_rollout_ref.actor.clamp_p=0.33 \
    actor_rollout_ref.actor.adaptive_entropy.entropy_low=0.15 \
    actor_rollout_ref.actor.adaptive_entropy.entropy_high=0.24 \
    actor_rollout_ref.actor.adaptive_entropy.entropy_coeff_lr=0.002 \
    actor_rollout_ref.actor.adaptive_entropy.entropy_coeff_reg=0 \
    actor_rollout_ref.actor.adaptive_entropy.entropy_coeff_warmup=30 \
    actor_rollout_ref.actor.adaptive_entropy.entropy_coeff_clip_high=0.009 \
    actor_rollout_ref.actor.adaptive_entropy.entropy_coeff_clip_low=0.0006 \
    actor_rollout_ref.actor.ppo_epochs=1 \
    actor_rollout_ref.actor.fsdp_config.param_offload=False \
    actor_rollout_ref.actor.fsdp_config.optimizer_offload=True \
    actor_rollout_ref.rollout.name=hf \
    actor_rollout_ref.rollout.dtype=float16 \
    actor_rollout_ref.rollout.tensor_model_parallel_size="${NUM_GPU}" \
    actor_rollout_ref.rollout.n=4 \
    actor_rollout_ref.rollout.log_prob_micro_batch_size_per_gpu=1 \
    actor_rollout_ref.rollout.val_kwargs.do_sample=True \
    actor_rollout_ref.rollout.val_kwargs.top_p=0.95 \
    actor_rollout_ref.rollout.val_kwargs.top_k=20 \
    actor_rollout_ref.rollout.val_kwargs.temperature=0.6 \
    actor_rollout_ref.rollout.val_kwargs.n=2 \
    actor_rollout_ref.ref.log_prob_micro_batch_size_per_gpu=1 \
    actor_rollout_ref.ref.entropy_checkpointing=True \
    actor_rollout_ref.ref.fsdp_config.param_offload=True \
    algorithm.kl_ctrl.kl_coef=0 \
    trainer.critic_warmup=0 \
    trainer.logger=['console','tensorboard','comet_ml'] \
    "trainer.project_name=${PROJECT_NAME}" \
    "trainer.experiment_name=${EXPERIMENT_NAME}" \
    trainer.n_gpus_per_node="${NUM_GPU}" \
    trainer.nnodes=1 \
    trainer.save_freq=0 \
    trainer.test_freq=10 \
    trainer.total_epochs=1 \
    "$@"
