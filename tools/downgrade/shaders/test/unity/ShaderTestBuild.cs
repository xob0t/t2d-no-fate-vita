using System;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

// Batch-mode entry points of the shader test (tools/downgrade/shaders/test/build.py):
//   Unity.exe -batchmode -projectPath <project> -executeMethod ShaderTestBuild.RenderD3D11 -outputFolder <folder>
//     renders every case with the editor's Direct3D 11 device (no -nographics: it needs the device);
//   Unity.exe -batchmode -nographics -buildTarget PSP2 -projectPath <project> -executeMethod ShaderTestBuild.BuildVita
//     -buildOutput <folder> -vitaTitleId TDSH00001
//     builds the PC Hosted PS Vita player, which compiles the shaders with the PS Vita's shader compiler.
public static class ShaderTestBuild
{
    const string ContentId = "IV0000-{0}_00-T2DSHADERTEST000";

    public static void RenderD3D11()
    {
        string folder = Argument("-outputFolder");
        if (folder == null) throw new ArgumentException("-outputFolder needed");
        PlayerSettings.colorSpace = ColorSpace.Gamma;
        ShaderTest.Log("RENDER gfx=" + SystemInfo.graphicsDeviceType + " " + SystemInfo.graphicsDeviceVersion);
        int errors = 0;
        foreach (string name in new[] { "Unlit/CRTShader", "Unlit/NormalShader", "Unlit/ColorTransformShader" })
        {
            Shader shader = Shader.Find(name);
            bool supported = shader != null && shader.isSupported;
            ShaderTest.Log("SHADER " + name + " found=" + (shader != null) + " supported=" + supported);
            if (!supported) errors++;
        }
        if (errors == 0) ShaderTest.RenderAll(folder);
        ShaderTest.Log("RENDER done errors=" + errors);
        EditorApplication.Exit(errors == 0 ? 0 : 1);
    }

    public static void BuildVita()
    {
        string output = Argument("-buildOutput");
        string titleId = Argument("-vitaTitleId");
        if (output == null || titleId == null || titleId.Length != 9) throw new ArgumentException("-buildOutput and -vitaTitleId (9 characters) needed");
        if (!EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.PSP2, BuildTarget.PSP2))
        {
            Debug.LogError("T2DSH BUILD Failed: no PS Vita build support");
            EditorApplication.Exit(2);
            return;
        }
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var go = new GameObject("ShaderTest");
        var cam = go.AddComponent<Camera>();
        cam.orthographic = true;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = Color.black;
        go.AddComponent<ShaderTest>();
        EditorSceneManager.SaveScene(scene, "Assets/Main.unity");
        AssetDatabase.SaveAssets();

        PlayerSettings.colorSpace = ColorSpace.Gamma;
        EditorUserBuildSettings.psp2BuildSubtarget = PSP2BuildSubtarget.PCHosted;
        PlayerSettings.PSVita.contentID = string.Format(ContentId, titleId);
        PlayerSettings.PSVita.shortTitle = "T2D ShaderTest";
        PlayerSettings.productName = "T2D ShaderTest";
        PlayerSettings.SetScriptingBackend(BuildTargetGroup.PSP2, ScriptingImplementation.IL2CPP);
        PlayerSettings.SplashScreen.show = false;
        PlayerSettings.SplashScreen.showUnityLogo = false;

        BuildReport report = BuildPipeline.BuildPlayer(new[] { "Assets/Main.unity" }, output, BuildTarget.PSP2, BuildOptions.None);
        Debug.Log("T2DSH BUILD " + report.summary.result + " errors=" + report.summary.totalErrors + " warnings=" + report.summary.totalWarnings);
        foreach (var step in report.steps)
            foreach (var m in step.messages.Where(m => m.type == LogType.Error || m.type == LogType.Exception || m.type == LogType.Warning))
                Debug.Log("T2DSH " + m.type.ToString().ToUpperInvariant() + " " + step.name + ": " + m.content);
        EditorApplication.Exit(report.summary.result == BuildResult.Succeeded ? 0 : 1);
    }

    static string Argument(string name)
    {
        string[] args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
