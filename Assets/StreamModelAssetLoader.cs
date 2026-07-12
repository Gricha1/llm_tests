using System;
using System.IO;
using System.Reflection;
using Unity.InferenceEngine;
using UnityEngine;

/// <summary>Читает/пишет .sentis (бинарный ModelAsset) с диска для hot reload на стриме.</summary>
public static class StreamModelAssetLoader
{
    static readonly Type WeightsDataType = Type.GetType("Unity.InferenceEngine.ModelAssetWeightsData, Unity.InferenceEngine");
    static readonly FieldInfo ModelAssetDataField = typeof(ModelAsset).GetField("modelAssetData", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    static readonly FieldInfo ModelWeightsChunksField = typeof(ModelAsset).GetField("modelWeightsChunks", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    static readonly FieldInfo DataValueField = Type.GetType("Unity.InferenceEngine.ModelAssetData, Unity.InferenceEngine")
        ?.GetField("value", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    public static ModelAsset LoadFromSentisFile(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return null;

        if (ModelAssetDataField == null || ModelWeightsChunksField == null || WeightsDataType == null || DataValueField == null)
        {
            Debug.LogError("[StreamModelAssetLoader] Inference Engine API недоступен");
            return null;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new BinaryReader(stream);

        var modelAsset = ScriptableObject.CreateInstance<ModelAsset>();
        var assetData = ScriptableObject.CreateInstance(Type.GetType("Unity.InferenceEngine.ModelAssetData, Unity.InferenceEngine"));
        DataValueField.SetValue(assetData, reader.ReadBytes(reader.ReadInt32()));
        ModelAssetDataField.SetValue(modelAsset, assetData);

        int chunkCount = reader.ReadInt32();
        var chunks = Array.CreateInstance(WeightsDataType, chunkCount);
        for (int i = 0; i < chunkCount; i++)
        {
            var chunk = ScriptableObject.CreateInstance(WeightsDataType);
            DataValueField.SetValue(chunk, reader.ReadBytes(reader.ReadInt32()));
            chunks.SetValue(chunk, i);
        }

        ModelWeightsChunksField.SetValue(modelAsset, chunks);
        modelAsset.name = Path.GetFileNameWithoutExtension(path);
        return modelAsset;
    }

    public static void SaveToSentisFile(ModelAsset modelAsset, string path)
    {
        if (modelAsset == null || ModelAssetDataField == null || ModelWeightsChunksField == null || DataValueField == null)
            return;

        var assetData = ModelAssetDataField.GetValue(modelAsset);
        var desc = DataValueField.GetValue(assetData) as byte[];
        if (desc == null || desc.Length == 0)
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        using var writer = new BinaryWriter(stream);

        writer.Write(desc.Length);
        writer.Write(desc);

        var chunksObj = ModelWeightsChunksField.GetValue(modelAsset) as Array;
        int chunkCount = chunksObj?.Length ?? 0;
        writer.Write(chunkCount);
        for (int i = 0; i < chunkCount; i++)
        {
            var chunk = chunksObj.GetValue(i);
            var chunkBytes = DataValueField.GetValue(chunk) as byte[] ?? Array.Empty<byte>();
            writer.Write(chunkBytes.Length);
            writer.Write(chunkBytes);
        }
    }
}
