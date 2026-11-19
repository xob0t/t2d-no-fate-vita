using System;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

// Batch-mode build of the PS Vita mesh test (tools/assess/vita-mesh-test/build.py):
//   Unity.exe -batchmode -nographics -buildTarget PSP2 -projectPath <project> -executeMethod MeshTestBuild.Build
//     -buildOutput <folder> -vitaTitleId TDMS00001
// Makes the sprite material (Resources/sprite.mat, instancing on) and the scene, and builds a PC Hosted player.
public static class MeshTestBuild
{
    const string ContentId = "IV0000-{0}_00-T2DMESHTEST00000";

    public static void Build()
    {
        string[] args = Environment.GetCommandLineArgs();
        string output = Argument(args, "-buildOutput");
        string titleId = Argument(args, "-vitaTitleId");
        if (output == null || titleId == null || titleId.Length != 9) throw new ArgumentException("-buildOutput and -vitaTitleId (9 characters) needed");

        if (!EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.PSP2, BuildTarget.PSP2))
        {
            Debug.LogError("MESHTEST_BUILD Failed: no PS Vita build support");
            EditorApplication.Exit(2);
            return;
        }

        const string matPath = "Assets/Resources/sprite.mat";
        var shader = Shader.Find("MeshTest/Sprite");
        if (shader == null) throw new Exception("MeshTest/Sprite shader not found");
        var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
        if (mat == null)
        {
            mat = new Material(shader);
            AssetDatabase.CreateAsset(mat, matPath);
        }
        mat.shader = shader;
        mat.enableInstancing = true;
        EditorUtility.SetDirty(mat);

        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var go = new GameObject("MeshTest");
        var cam = go.AddComponent<Camera>();
        cam.orthographic = true;
        go.AddComponent<MeshTest>();
        EditorSceneManager.SaveScene(scene, "Assets/Main.unity");
        AssetDatabase.SaveAssets();

        // Batching as the PS Vita player is built with (PlayerSettings keeps it per platform, internally).
        MethodInfo get = typeof(PlayerSettings).GetMethod("GetBatchingForPlatform", BindingFlags.Static | BindingFlags.NonPublic);
        // -dynamicBatching 0|1 sets it (the default build leaves it as the project has it).
        string dynamic = Argument(args, "-dynamicBatching");
        MethodInfo set = typeof(PlayerSettings).GetMethod("SetBatchingForPlatform", BindingFlags.Static | BindingFlags.NonPublic);
        if (dynamic != null && set != null) set.Invoke(null, new object[] { BuildTarget.PSP2, 0, int.Parse(dynamic) });
        if (get != null)
        {
            object[] p = { BuildTarget.PSP2, 0, 0 };
            get.Invoke(null, p);
            Debug.Log("MESHTEST_BATCHING static=" + p[1] + " dynamic=" + p[2] + (dynamic != null && set == null ? " (no SetBatchingForPlatform)" : ""));
        }

        EditorUserBuildSettings.psp2BuildSubtarget = PSP2BuildSubtarget.PCHosted;
        PlayerSettings.PSVita.contentID = string.Format(ContentId, titleId);
        PlayerSettings.PSVita.shortTitle = "T2D MeshTest";
        PlayerSettings.productName = "T2D MeshTest";
        PlayerSettings.PSVita.memoryExpansionMode = PlayerSettings.PSVita.PSVitaMemoryExpansionMode.ExpandBy109MB;
        PlayerSettings.SetScriptingBackend(BuildTargetGroup.PSP2, ScriptingImplementation.IL2CPP);
        PlayerSettings.SplashScreen.show = false;
        PlayerSettings.SplashScreen.showUnityLogo = false;

        BuildReport report = BuildPipeline.BuildPlayer(new[] { "Assets/Main.unity" }, output, BuildTarget.PSP2, BuildOptions.None);
        Debug.Log("MESHTEST_BUILD " + report.summary.result + " errors=" + report.summary.totalErrors + " size=" + report.summary.totalSize);
        foreach (var step in report.steps)
            foreach (var m in step.messages.Where(m => m.type == LogType.Error || m.type == LogType.Exception))
                Debug.Log("MESHTEST_ERROR " + step.name + ": " + m.content);
        EditorApplication.Exit(report.summary.result == BuildResult.Succeeded ? 0 : 1);
    }

    static string Argument(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
