import argparse
import os
import re

import datasets


def extract_solution(solution_str: str) -> str:
    """
    Extract the final boxed answer from a MATH solution.

    We intentionally keep this script independent of the `verl` package import path,
    so it can run directly from a mounted repo without `pip install -e .`.
    """
    if solution_str is None:
        return ""

    # Take the *last* boxed expression if present.
    # Common patterns: \boxed{...} or \\boxed{...} depending on escaping.
    matches = re.findall(r"\\boxed\{([^}]*)\}", solution_str)
    if matches:
        return matches[-1].strip()

    # Fallback: sometimes boxed is written without braces or with spaces.
    m = re.search(r"\\boxed\s*\{([^}]*)\}", solution_str)
    if m:
        return m.group(1).strip()

    return str(solution_str).strip()


def main() -> None:
    parser = argparse.ArgumentParser(description="Prepare a small MATH parquet dataset for verl/AEnt smoke tests.")
    parser.add_argument("--out_dir", default="datasets/processed_math", help="Output directory (relative to repo root).")
    parser.add_argument("--train_n", type=int, default=512, help="Number of training examples to keep (smoke test).")
    parser.add_argument("--test_n", type=int, default=128, help="Number of test examples to keep (smoke test).")
    parser.add_argument("--seed", type=int, default=1)
    args = parser.parse_args()

    data_source = "DigitalLearningGmbH/MATH-lighteval"
    ds = datasets.load_dataset(data_source)
    train_ds = ds["train"]
    test_ds = ds["test"]

    # Make it deterministic & small.
    train_ds = train_ds.shuffle(seed=args.seed).select(range(min(args.train_n, len(train_ds))))
    test_ds = test_ds.shuffle(seed=args.seed).select(range(min(args.test_n, len(test_ds))))

    instruction = "Let's think step by step and output the final answer within \\boxed{}."

    def map_fn(split: str):
        def _fn(example, idx: int):
            # Fields are typically: problem, solution, level, subject, etc.
            problem = example.get("problem") or example.get("question") or example.get("prompt")
            solution_raw = example.get("solution") or example.get("answer")
            if problem is None or solution_raw is None:
                raise ValueError(f"Unexpected schema keys={list(example.keys())}")

            prompt = f"{problem} {instruction}"
            gt = extract_solution(solution_raw)
            return {
                "data_source": data_source,
                "prompt": [{"role": "user", "content": prompt}],
                "ability": "math",
                "reward_model": {"style": "rule", "ground_truth": gt},
                "extra_info": {"split": split, "index": idx},
            }

        return _fn

    train_ds = train_ds.map(map_fn("train"), with_indices=True, remove_columns=train_ds.column_names)
    test_ds = test_ds.map(map_fn("test"), with_indices=True, remove_columns=test_ds.column_names)

    out_dir = os.path.abspath(args.out_dir)
    os.makedirs(out_dir, exist_ok=True)
    train_path = os.path.join(out_dir, "train.parquet")
    test_path = os.path.join(out_dir, "test.parquet")

    train_ds.to_parquet(train_path)
    test_ds.to_parquet(test_path)

    print(f"Wrote: {train_path}")
    print(f"Wrote: {test_path}")


if __name__ == "__main__":
    main()

