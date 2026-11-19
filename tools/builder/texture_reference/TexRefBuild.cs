using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

// The editor's half of tools/builder/check_textures.py reference: builds the textures the check generated, with
// the import settings of their .meta files as written (it changes no importer), into
//   <output>/bundles/texref   an AssetBundle of Assets/Bundled (BuildAssetBundles, as the pipeline's bundles are)
//   <output>/                  a PC Hosted PS Vita player, whose resources.assets holds Assets/Resources/tex
//   Unity.exe -batchmode -nographics -buildTarget PSP2 -projectPath <project> -executeMethod TexRefBuild.Build
//     -buildOutput <folder> -vitaTitleId TDRF00001
public static class TexRefBuild
{
    const string ContentId = "IV0000-{0}_00-T2DTEXREF0000000";

    public static void Build()
    {
        string[] args = Environment.GetCommandLineArgs();
        string output = Argument(args, "-buildOutput");
        string titleId = Argument(args, "-vitaTitleId");
        if (output == null || titleId == null || titleId.Length != 9) throw new ArgumentException("-buildOutput and -vitaTitleId (9 characters) needed");
        if (!EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.PSP2, BuildTarget.PSP2))
        {
            Debug.LogError("TEXREF_BUILD Failed: no PS Vita build support");
            EditorApplication.Exit(2);
            return;
        }
        foreach (string folder in new[] { "Assets/Resources/tex", "Assets/Bundled" })
            foreach (string path in Directory.GetFiles(folder, "*.png").OrderBy(p => p, StringComparer.Ordinal))
            {
                string asset = path.Replace('\\', '/');
                var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(asset);
                Debug.Log("TEXREF_IMPORT " + asset + " " + (tex == null ? "none" : tex.width + "x" + tex.height + " " + tex.format + " mips=" + tex.mipmapCount));
            }

        string bundles = Path.Combine(output, "bundles");
        Directory.CreateDirectory(bundles);
        var bundle = new AssetBundleBuild
        {
            assetBundleName = "texref",
            assetNames = Directory.GetFiles("Assets/Bundled", "*.png").Select(p => p.Replace('\\', '/')).OrderBy(p => p, StringComparer.Ordinal).ToArray(),
        };
        var manifest = BuildPipeline.BuildAssetBundles(bundles, new[] { bundle },
            BuildAssetBundleOptions.UncompressedAssetBundle | BuildAssetBundleOptions.DeterministicAssetBundle, BuildTarget.PSP2);
        Debug.Log("TEXREF_BUNDLES " + (manifest == null ? "failed" : string.Join(",", manifest.GetAllAssetBundles())));

        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        new GameObject("Camera").AddComponent<Camera>();
        EditorSceneManager.SaveScene(scene, "Assets/Main.unity");
        AssetDatabase.SaveAssets();

        EditorUserBuildSettings.psp2BuildSubtarget = PSP2BuildSubtarget.PCHosted;
        PlayerSettings.PSVita.contentID = string.Format(ContentId, titleId);
        PlayerSettings.PSVita.shortTitle = "T2D TexRef";
        PlayerSettings.productName = "T2D TexRef";
        PlayerSettings.SetScriptingBackend(BuildTargetGroup.PSP2, ScriptingImplementation.IL2CPP);
        PlayerSettings.SplashScreen.show = false;
        PlayerSettings.SplashScreen.showUnityLogo = false;
        BuildReport report = BuildPipeline.BuildPlayer(new[] { "Assets/Main.unity" }, Path.Combine(output, "player"), BuildTarget.PSP2, BuildOptions.None);
        Debug.Log("TEXREF_BUILD " + report.summary.result + " errors=" + report.summary.totalErrors);
        foreach (var step in report.steps)
            foreach (var m in step.messages.Where(m => m.type == LogType.Error || m.type == LogType.Exception))
                Debug.Log("TEXREF_ERROR " + step.name + ": " + m.content);
        EditorApplication.Exit(report.summary.result == BuildResult.Succeeded && manifest != null ? 0 : 1);
    }

    static string Argument(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
