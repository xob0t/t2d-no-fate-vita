using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

// Batch-mode build of the PS Vita audio test (tools/downgrade/audio/vita-audio-test/build.py):
//   Unity.exe -batchmode -nographics -buildTarget PSP2 -projectPath <project> -executeMethod AudioTestBuild.Build
//     -buildOutput <folder> -vitaTitleId TDAU00001
// The clips' import settings come from their .meta files (build.py writes them with audio_rules.py). Logs each
// clip's PS Vita import (AUDIOTEST_IMPORT), makes the scene and builds a PC Hosted player.
public static class AudioTestBuild
{
    const string ContentId = "IV0000-{0}_00-T2DAUDIOTEST0000";

    public static void Build()
    {
        string[] args = Environment.GetCommandLineArgs();
        string output = Argument(args, "-buildOutput");
        string titleId = Argument(args, "-vitaTitleId");
        if (output == null || titleId == null || titleId.Length != 9) throw new ArgumentException("-buildOutput and -vitaTitleId (9 characters) needed");

        if (!EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.PSP2, BuildTarget.PSP2))
        {
            Debug.LogError("AUDIOTEST_BUILD Failed: no PS Vita build support");
            EditorApplication.Exit(2);
            return;
        }
        AssetDatabase.Refresh();

        // What the importer made of the probe clips (the rest follow the same few settings).
        foreach (string guid in AssetDatabase.FindAssets("t:AudioClip", new[] { "Assets/Resources/probe" }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            var importer = (AudioImporter)AssetImporter.GetAtPath(path);
            AudioImporterSampleSettings s = importer.GetOverrideSampleSettings("PSP2");
            var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
            Debug.Log("AUDIOTEST_IMPORT " + path + " psp2: load=" + s.loadType + " format=" + s.compressionFormat + " rate=" + s.sampleRateSetting + "/" + s.sampleRateOverride +
                      " mono=" + importer.forceToMono + " preload=" + importer.preloadAudioData +
                      " clip=" + (clip == null ? "none" : clip.frequency + "Hz " + clip.channels + "ch " + clip.samples + " samples " + clip.loadType));
        }

        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var go = new GameObject("AudioTest");
        var cam = go.AddComponent<Camera>();
        cam.orthographic = true;
        cam.clearFlags = CameraClearFlags.SolidColor;
        go.AddComponent<AudioListener>();
        go.AddComponent<AudioTest>();
        EditorSceneManager.SaveScene(scene, "Assets/Main.unity");
        AssetDatabase.SaveAssets();

        EditorUserBuildSettings.psp2BuildSubtarget = PSP2BuildSubtarget.PCHosted;
        PlayerSettings.PSVita.contentID = string.Format(ContentId, titleId);
        PlayerSettings.PSVita.shortTitle = "T2D AudioTest";
        PlayerSettings.productName = "T2D AudioTest";
        PlayerSettings.PSVita.memoryExpansionMode = PlayerSettings.PSVita.PSVitaMemoryExpansionMode.ExpandBy109MB;
        PlayerSettings.SetScriptingBackend(BuildTargetGroup.PSP2, ScriptingImplementation.IL2CPP);
        PlayerSettings.SplashScreen.show = false;
        PlayerSettings.SplashScreen.showUnityLogo = false;

        BuildReport report = BuildPipeline.BuildPlayer(new[] { "Assets/Main.unity" }, output, BuildTarget.PSP2, BuildOptions.None);
        Debug.Log("AUDIOTEST_BUILD " + report.summary.result + " errors=" + report.summary.totalErrors + " size=" + report.summary.totalSize);
        foreach (var step in report.steps)
            foreach (var m in step.messages.Where(m => m.type == LogType.Error || m.type == LogType.Exception))
                Debug.Log("AUDIOTEST_ERROR " + step.name + ": " + m.content);
        EditorApplication.Exit(report.summary.result == BuildResult.Succeeded ? 0 : 1);
    }

    static string Argument(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
