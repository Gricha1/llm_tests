#if UNITY_EDITOR
using System;
using System.IO;
using Unity.InferenceEngine;
using UnityEditor;
using UnityEngine;

/// <summary>Batch: ONNX из stream_weights → .sentis для hot reload (Unity не перезапускается).</summary>
public static class ForestStreamSentisExporter
{
    static readonly string[] BehaviorNames =
    {
        "JackLowLevelAgent",
        "LilyLowLevelAgent",
        "GeorgeLowLevelAgent",
    };

    const string ExportRoot = "Assets/StreamExport/_batch";

    public static void ExportFromEnv()
    {
        var onnxDir = Environment.GetEnvironmentVariable("FOREST_STREAM_ONNX_DIR");
        var sentisDir = Environment.GetEnvironmentVariable("FOREST_STREAM_SENTIS_DIR");
        if (string.IsNullOrEmpty(onnxDir) || string.IsNullOrEmpty(sentisDir))
        {
            Debug.LogError("[ForestStreamSentisExporter] нужны FOREST_STREAM_ONNX_DIR и FOREST_STREAM_SENTIS_DIR");
            EditorApplication.Exit(1);
            return;
        }

        Directory.CreateDirectory(ExportRoot);
        Directory.CreateDirectory(sentisDir);

        int exported = 0;
        foreach (var behavior in BehaviorNames)
        {
            var srcOnnx = Path.Combine(onnxDir, behavior + ".onnx");
            if (!File.Exists(srcOnnx))
                continue;

            var assetOnnx = $"{ExportRoot}/{behavior}.onnx";
            File.Copy(srcOnnx, assetOnnx, true);
            AssetDatabase.ImportAsset(assetOnnx, ImportAssetOptions.ForceUpdate);

            var modelAsset = AssetDatabase.LoadAssetAtPath<ModelAsset>(assetOnnx);
            if (modelAsset == null)
            {
                Debug.LogWarning($"[ForestStreamSentisExporter] import failed: {assetOnnx}");
                continue;
            }

            var dstSentis = Path.Combine(sentisDir, behavior + ".sentis");
            StreamModelAssetLoader.SaveToSentisFile(modelAsset, dstSentis);
            Debug.Log($"[ForestStreamSentisExporter] {srcOnnx} -> {dstSentis}");
            exported++;
        }

        AssetDatabase.DeleteAsset(ExportRoot);
        Debug.Log($"[ForestStreamSentisExporter] done exported={exported}");
        EditorApplication.Exit(exported > 0 ? 0 : 2);
    }
}
#endif
