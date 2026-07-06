using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>Снимки для оверлея метрик: энтропия действий, приближение grad norm.</summary>
public static class TrainingPolicyStats
{
    const int MaxEntropySamples = 120;
    const int MaxGradSamples = 120;

    static readonly int[] MoveCounts = new int[4];
    static readonly int[] RotateCounts = new int[4];
    static readonly int[] ChopCounts = new int[2];
    static int _actionSamples;

    static readonly List<float> EntropyHistory = new List<float>();
    static readonly List<float> GradNormHistory = new List<float>();
    static float _lastRecordedEma = float.NaN;

    public static IReadOnlyList<float> EntropySeries => EntropyHistory;
    public static IReadOnlyList<float> GradNormSeries => GradNormHistory;
    public static float LastEntropy { get; private set; }
    public static float LastGradNormApprox { get; private set; }

    public static void RecordJackActions(int move, int rotate, int chop)
    {
        move = Mathf.Clamp(move, 0, 3);
        rotate = Mathf.Clamp(rotate, 0, 3);
        chop = Mathf.Clamp(chop, 0, 1);

        MoveCounts[move]++;
        RotateCounts[rotate]++;
        ChopCounts[chop]++;
        _actionSamples++;

        if (_actionSamples % 32 != 0)
            return;

        float entropy = (BranchEntropy(MoveCounts) + BranchEntropy(RotateCounts) + BranchEntropy(ChopCounts)) / 3f;
        LastEntropy = entropy;
        PushSample(EntropyHistory, entropy, MaxEntropySamples);
    }

    public static void NotifyEpisodeRewardEma(float ema)
    {
        if (float.IsNaN(_lastRecordedEma))
        {
            _lastRecordedEma = ema;
            return;
        }

        float gradApprox = Mathf.Abs(ema - _lastRecordedEma);
        LastGradNormApprox = gradApprox;
        PushSample(GradNormHistory, gradApprox, MaxGradSamples);
        _lastRecordedEma = ema;
    }

    static float BranchEntropy(int[] counts)
    {
        int total = 0;
        for (int i = 0; i < counts.Length; i++)
            total += counts[i];

        if (total <= 0)
            return 0f;

        float h = 0f;
        for (int i = 0; i < counts.Length; i++)
        {
            if (counts[i] <= 0)
                continue;

            float p = counts[i] / (float)total;
            h -= p * Mathf.Log(p, 2f);
        }

        return h;
    }

    static void PushSample(List<float> list, float value, int max)
    {
        list.Add(value);
        if (list.Count > max)
            list.RemoveAt(0);
    }
}
