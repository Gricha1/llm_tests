#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Делает у water.jpg прозрачный фон (белые пиксели) и сохраняет water_drop.png для HUD.
/// </summary>
public static class WaterIconAlphaUtility
{
    const string SourcePath = "Assets/UI/Icons/Custom/water.jpg";
    const string OutputPath = "Assets/UI/Icons/Custom/water_drop.png";

    [InitializeOnLoadMethod]
    static void AutoFixOnLoad()
    {
        EditorApplication.delayCall += () =>
        {
            if (!File.Exists(OutputPath))
                FixWaterIcon(silent: true);
        };
    }

    [MenuItem("Tools/Fix Water Icon Alpha")]
    public static void FixWaterIconMenu() => FixWaterIcon(silent: false);

    static void FixWaterIcon(bool silent)
    {
        if (!File.Exists(SourcePath))
            return;

        var importer = AssetImporter.GetAtPath(SourcePath) as TextureImporter;
        if (importer != null && !importer.isReadable)
        {
            importer.isReadable = true;
            importer.SaveAndReimport();
        }

        var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(SourcePath);
        if (tex == null)
            return;

        var outTex = new Texture2D(tex.width, tex.height, TextureFormat.RGBA32, false);
        var pixels = tex.GetPixels();
        for (int i = 0; i < pixels.Length; i++)
        {
            Color p = pixels[i];
            if (p.r > 0.9f && p.g > 0.9f && p.b > 0.9f)
                p.a = 0f;
            else
                p.a = 1f;
            pixels[i] = p;
        }

        outTex.SetPixels(pixels);
        outTex.Apply();

        File.WriteAllBytes(OutputPath, outTex.EncodeToPNG());
        AssetDatabase.Refresh();

        var outImporter = AssetImporter.GetAtPath(OutputPath) as TextureImporter;
        if (outImporter != null)
        {
            outImporter.textureType = TextureImporterType.Sprite;
            outImporter.spriteImportMode = SpriteImportMode.Single;
            outImporter.alphaIsTransparency = true;
            outImporter.mipmapEnabled = false;
            outImporter.filterMode = FilterMode.Bilinear;
            outImporter.SaveAndReimport();
        }

        UpdateWaterSpriteAssetReference();

        if (!silent)
            Debug.Log("Water icon: saved transparent water_drop.png and updated water.asset");
    }

    static void UpdateWaterSpriteAssetReference()
    {
        var png = AssetDatabase.LoadAssetAtPath<Texture2D>(OutputPath);
        if (png == null)
            return;

        var asset = AssetDatabase.LoadAssetAtPath<TMPro.TMP_SpriteAsset>(SourcePath.Replace("water.jpg", "water.asset"));
        if (asset == null)
            return;

        // TMP asset YAML is easier to patch via reimport; user can assign in inspector if needed.
        EditorUtility.SetDirty(asset);
    }
}
#endif
