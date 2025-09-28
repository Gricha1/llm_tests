# AEnt: Adaptive entropy control with token space clamping

This repo provides an implementation of AEnt, a LLM-RL algorithm that enables more efficient entropy control in LLM setting. AEnt uses an entropy loss with clamped token space, which encourages exploration within a more compact and reasonable response space. This reduces the bias induced by entropy, while still leveraging the beneficial smoothing effect of the entropy loss. Furthermore, AEnt adjusts the entropy coefficient automatically during training, thus overcoming the challenge that entropy is difficult to control due to its sensitivity to the entropy coefficient.

## Structure
```
. # this implementation is built on verl-0.4.1
├──recipe/aent
│  ├── config/  # contains AEnt hyper-params
│  └── main_aent.py
│  └── ray_aent_trainer.py  # initialize and call aent_fsdp_workers to update policy; update entropy coefficient
│  └── aent_fsdp_workers.py  # initialize and call aent_dp_actors to update policy parameter
│  └── aent_dp_actor.py  # update policy by optimizing (policy loss - entropy coefficient * clamped entropy loss)
│  └── aent_torch_functional.py  # implement clamped entropy loss
│  └── run_aent_math.sh  # test run on MATH dataset, originally run on 4xA100
│  └── run_aent_openr1.sh  # test run on Openr1-math dataset, originally run on 8xA100
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
