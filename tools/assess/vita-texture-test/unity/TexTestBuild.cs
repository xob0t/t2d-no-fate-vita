using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

// Batch-mode build of the PS Vita texture test (tools/assess/vita-texture-test/build.py):
//   Unity.exe -batchmode -nographics -buildTarget PSP2 -projectPath <project> -executeMethod TexTestBuild.Build
//     -buildOutput <folder> -vitaTitleId T2DT00001
// Sets each Assets/Resources/tex/tp0_<format>_<size>.png's PS Vita import format from its name, makes the scene
// and the blit material, and builds a PC Hosted player.
public static class TexTestBuild
{
    const string ContentId = "IV0000-{0}_00-T2DTEXTURETEST00";

    static TextureImporterFormat FormatOf(string name)
    {
        if (name.Contains("_dxt5_")) return TextureImporterFormat.DXT5;
        if (name.Contains("_rgba4444_")) return TextureImporterFormat.RGBA16;
        if (name.Contains("_pvrtc4_")) return TextureImporterFormat.PVRTC_RGBA4;
        if (name.Contains("_pvrtc2_")) return TextureImporterFormat.PVRTC_RGBA2;
        if (name.Contains("_rgba32_")) return TextureImporterFormat.RGBA32;
        throw new ArgumentException("no format in " + name);
    }

    public static void Build()
    {
        string[] args = Environment.GetCommandLineArgs();
        string output = Argument(args, "-buildOutput");
        string titleId = Argument(args, "-vitaTitleId");
        if (output == null || titleId == null || titleId.Length != 9) throw new ArgumentException("-buildOutput and -vitaTitleId (9 characters) needed");

        if (!EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.PSP2, BuildTarget.PSP2))
        {
            Debug.LogError("TEXTEST_BUILD Failed: no PS Vita build support");
            EditorApplication.Exit(2);
            return;
        }

        foreach (string path in Directory.GetFiles("Assets/Resources/tex", "*.png"))
        {
            string asset = path.Replace('\\', '/');
            var importer = (TextureImporter)AssetImporter.GetAtPath(asset);
            string name = Path.GetFileNameWithoutExtension(asset);
            int size = name.EndsWith("_2048") ? 2048 : 4096;
            importer.textureType = TextureImporterType.Default;
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.mipmapEnabled = false;
            importer.isReadable = false;
            importer.alphaIsTransparency = false;
            importer.filterMode = FilterMode.Point;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.maxTextureSize = size;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.crunchedCompression = false;
            var vita = new TextureImporterPlatformSettings
            {
                name = "PSP2", overridden = true, maxTextureSize = size, format = FormatOf(name),
                textureCompression = TextureImporterCompression.Compressed, compressionQuality = 100, crunchedCompression = false,
            };
            importer.SetPlatformTextureSettings(vita);
            importer.SaveAndReimport();
            TextureImporterPlatformSettings got = importer.GetPlatformTextureSettings("PSP2");
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(asset);
            Debug.Log("TEXTEST_IMPORT " + asset + " psp2=" + got.overridden + " " + got.format + " max=" + got.maxTextureSize +
                      " imported=" + (tex == null ? "none" : tex.width + "x" + tex.height + " " + tex.format));
        }

        const string blitPath = "Assets/Resources/blit.mat";
        if (AssetDatabase.LoadAssetAtPath<Material>(blitPath) == null)
            AssetDatabase.CreateAsset(new Material(Shader.Find("Sprites/Default")), blitPath);

        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var go = new GameObject("TexTest");
        var cam = go.AddComponent<Camera>();
        cam.orthographic = true;
        cam.clearFlags = CameraClearFlags.SolidColor;
        go.AddComponent<TexTest>();
        EditorSceneManager.SaveScene(scene, "Assets/Main.unity");
        AssetDatabase.SaveAssets();

        EditorUserBuildSettings.psp2BuildSubtarget = PSP2BuildSubtarget.PCHosted;
        PlayerSettings.PSVita.contentID = string.Format(ContentId, titleId);
        PlayerSettings.PSVita.shortTitle = "T2D TexTest";
        PlayerSettings.productName = "T2D TexTest";
        PlayerSettings.PSVita.memoryExpansionMode = PlayerSettings.PSVita.PSVitaMemoryExpansionMode.ExpandBy109MB;
        PlayerSettings.SetScriptingBackend(BuildTargetGroup.PSP2, ScriptingImplementation.IL2CPP);
        PlayerSettings.SplashScreen.show = false;
        PlayerSettings.SplashScreen.showUnityLogo = false;

        BuildReport report = BuildPipeline.BuildPlayer(new[] { "Assets/Main.unity" }, output, BuildTarget.PSP2, BuildOptions.None);
        Debug.Log("TEXTEST_BUILD " + report.summary.result + " errors=" + report.summary.totalErrors + " size=" + report.summary.totalSize);
        foreach (var step in report.steps)
            foreach (var m in step.messages.Where(m => m.type == LogType.Error || m.type == LogType.Exception))
                Debug.Log("TEXTEST_ERROR " + step.name + ": " + m.content);
        EditorApplication.Exit(report.summary.result == BuildResult.Succeeded ? 0 : 1);
    }

    static string Argument(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
