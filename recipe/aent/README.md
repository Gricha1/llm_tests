## Overview

This is a verl-based implementation of [AEnt](https://www.arxiv.org/pdf/2509.03493), a
clamped entropy regularization method for LLM-RL algorithms.

Entropy regularization has been a successful method for robotic and games RL, while it
offers weak gains for LLM RL. It is argued in the
[paper](https://www.arxiv.org/pdf/2509.03493) that entropy regularization suffers from
LLM tasks' sparse optimality and the immense response set.

> One can observe this effect in a toy run on a synthetic MDP below, where as the number
> of optimal actions decrease (thus sparsity increases), entropy regularization no
> longer has an advantage over no regularization. The method to be proposed is more
> robust to this issue.
>
> <div align="left">

<img src="https://github.com/hanshen95/hanshen95.github.io/blob/master/images/toy_demo.png?raw=true" alt="issue" style="width: 96%; height: auto;">
</div>

To address this issue, AEnt utilizes a clamped entropy regularization paired with
adaptively adjusted coefficient. It is observed that AEnt achieves larger gains on
multiple benchmarks when tested on different models and training datasets.

> An example run on DeepSeek-R1-distilled-Qwen-1.5b on 40k verifiable samples from
> Openr1-math dataset
>
> <div align="left">

<img src="https://github.com/hanshen95/hanshen95.github.io/blob/master/images/score_demo.png?raw=true" alt="issue" style="width: 96%; height: auto;">
</div>

## Configuration

The following configs and code files are in `recipe/aent/`.

### Clamped entropy

Related configs:

```yaml
actor:
    entropy_coeff: 0.0002
    clamp_entropy: True
    clamp_p: 0.3
```

`entropy_coeff` weighs the entropy regularization and `entropy_clamp` specifies value
clamping percentage p in the paper.

Related code in `aent_torch_functional.py`:

```python
def clamped_entropy_from_logits(logits: torch.Tensor, clamp_p: float):
    """Calculate entropy from logits with token space clamping."""
    logits_cpu = logits.cpu().detach()
    with torch.no_grad():   
        k = int(logits_cpu.size(-1)*clamp_p)
        _, rm_indices = torch.topk(logits_cpu,k=k,dim=-1,largest=False)
        row_indices = torch.arange(logits_cpu.size(0)).unsqueeze(1)
        rm_mask = torch.zeros_like(logits_cpu,dtype=torch.bool)
        rm_mask[row_indices,rm_indices]=True
        del logits_cpu, row_indices, rm_indices
    clamped_logits = logits.masked_fill(rm_mask.to(logits.device), -torch.inf)
    del rm_mask
    torch.cuda.empty_cache()
    clamped_pd = torch.nn.functional.softmax(clamped_logits, dim=-1)
    clamped_entropy = torch.logsumexp(clamped_logits, dim=-1) - torch.sum(clamped_pd * logits, dim=-1)
    return clamped_entropy
```

which calculates the `entropy_loss` in `aent_dp_actor.py`:

```python
policy_loss = pg_loss - entropy_loss * entropy_coeff
```

### Adaptive coefficient

Related configs:

```yaml
actor:
    adaptive_entropy:
        entropy_low: -1 
        entropy_high: -1 
        entropy_coeff_lr: -1 
        entropy_coeff_warmup: 0 
        entropy_coeff_clip_high: 0.1 
        entropy_coeff_clip_low: 0.00004
        # optional, the l2 regularization constant used in coeff update
        entropy_coeff_reg: 0 
```
A constant coeff will be used by default. Modify these args to enable adaptive coeff.
 `entropy_low/high`
sets the lower/upper tolerance of the clamped entropy, `entropy_coeff_clip_high/low` sets the
bounding interval of the coefficient. The coeffcient will start updating after
`entropy_coeff_warmup` with a learning rate of `entropy_coeff_lr`. We find that the algorithm is mostly just sensitive to `entropy_high/low`.


Related code in `ray_aent_trainer.py`:

```python
# entropy coeff update
if self.adaptive_entropy_control and self.entropy_coeff_warmup<=self.global_steps:
    entropy = float(metrics['actor/entropy_loss'])
    self.entropy_coeff -= self.entropy_coeff_lr*(min(0,entropy-self.entropy_box[0])+max(0,entropy-self.entropy_box[1]) \
                            +self.entropy_coeff_reg*(self.entropy_coeff-self.initial_entropy_coeff))
    self.entropy_coeff = min(max(self.entropy_coeff, self.entropy_coeff_box[0]), self.entropy_coeff_box[1])
    metrics.update({'actor/entropy_coeff': self.entropy_coeff})
```

## Example case
We may do two test runs, one on MATH, and another sligntly larger scaled one on openr1-math.
The training and test datasets have to be pre-processed following the examples in `examples/data_preprocess/'. One may also refer to [verl data processing guide](https://verl.readthedocs.io/en/latest/preparation/prepare_data.html#prepare-data-for-post-training).

Base model: Qwen-2.5-math-1.5b;
Training dataset: [MATH](https://huggingface.co/datasets/DigitalLearningGmbH/MATH-lighteval);
Test dataset: [MATH-500](https://huggingface.co/datasets/HuggingFaceH4/MATH-500), [MATH-Hard](https://huggingface.co/datasets/lighteval/MATH-Hard), [AMC23](https://huggingface.co/datasets/math-ai/amc23), [AIME-2024](https://huggingface.co/datasets/HuggingFaceH4/aime_2024), [MinervaMath](https://huggingface.co/datasets/math-ai/minervamath) and [OlympiadBench](https://huggingface.co/datasets/ScaleFrontierData/olympiadbench).
Reward: We use [math_verify](https://github.com/huggingface/Math-Verify), which can be enabled/disabled in `verl/utils/reward_score/__init__.py`.
With the correctly formated dataset, one may run
```bash
UNIQUEID=$(date +%s) PROJECT='aent_math' EXPERIMENT="run_$UNIQUEID" && mkdir -p "logs/$PROJECT" && export PROJECT EXPERIMENT && bash recipe/aent/run_aent_math.sh > >(tee "logs/$PROJECT/$EXPERIMENT.log") 2> >(tee "logs/$PROJECT/$EXPERIMENT.err" >&2)
```

A slightly larger scale run:
Base model: [Deepseek-R1-distilled-qwen-1.5b](https://huggingface.co/deepseek-ai/DeepSeek-R1-Distill-Qwen-1.5B);
Training dataset: 40k examples in [Openr1-math](https://huggingface.co/datasets/open-r1/OpenR1-Math-220k);
Test dataset: Same as the former test run.
Reward: Same as the former test run.
With the correctly formated dataset, one may run
```bash
UNIQUEID=$(date +%s) PROJECT='aent_openr1' EXPERIMENT="run_$UNIQUEID" && mkdir -p "logs/$PROJECT" && export PROJECT EXPERIMENT && bash recipe/aent/run_aent_openr1.sh > >(tee "logs/$PROJECT/$EXPERIMENT.log") 2> >(tee "logs/$PROJECT/$EXPERIMENT.err" >&2)
```


## Citation

```bibtex
@article{shen2025entropy,
  title={On Entropy Control in LLM-RL Algorithms},
  author={Shen, Han},
  journal={arXiv preprint arXiv:2509.03493},
  year={2025}
}
```