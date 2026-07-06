#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

/// <summary>
/// Большой mp3 (~1 ч) — Streaming, чтобы не грузить целиком в RAM.
/// </summary>
public sealed class CelticMusicImportSettings : AssetPostprocessor
{
    void OnPreprocessAudio()
    {
        if (!assetPath.Replace('\\', '/').EndsWith("Resources/Music/CelticElfMusic.mp3"))
            return;

        var importer = (AudioImporter)assetImporter;
        var settings = importer.defaultSampleSettings;
        settings.loadType = AudioClipLoadType.Streaming;
        settings.compressionFormat = AudioCompressionFormat.Vorbis;
        settings.quality = 0.7f;
        importer.defaultSampleSettings = settings;
        importer.loadInBackground = true;
        importer.forceToMono = true;
    }
}
#endif
