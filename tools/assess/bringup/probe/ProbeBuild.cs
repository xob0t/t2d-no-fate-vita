// Batch-mode build entry for the probe project (Assets/Editor):
//   Unity -batchmode -nographics -projectPath <p> -executeMethod ProbeBuild.Build -buildOutput <exe> -logFile <log>
// Builds the build settings' scenes as a Windows x64 Mono player, windowed, without the splash screen.
using System;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

public static class ProbeBuild
{
    public static void Build()
    {
        string[] args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, "-buildOutput");
        if (i < 0 || i + 1 >= args.Length) throw new ArgumentException("Missing -buildOutput <exe>");
        string output = args[i + 1];
        PlayerSettings.SetScriptingBackend(BuildTargetGroup.Standalone, ScriptingImplementation.Mono2x);
        PlayerSettings.SplashScreen.show = false;
        PlayerSettings.SplashScreen.showUnityLogo = false;
        PlayerSettings.runInBackground = true;
        PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
        PlayerSettings.defaultScreenWidth = 480;
        PlayerSettings.defaultScreenHeight = 270;
        PlayerSettings.displayResolutionDialog = ResolutionDialogSetting.Disabled;
        string[] scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
        BuildReport report = BuildPipeline.BuildPlayer(scenes, output, BuildTarget.StandaloneWindows64, BuildOptions.None);
        Debug.Log("PROBE_BUILD " + report.summary.result + " errors=" + report.summary.totalErrors + " scenes=" + scenes.Length +
                  " size=" + report.summary.totalSize + " time=" + report.summary.totalTime);
        foreach (var step in report.steps)
            foreach (var m in step.messages)
                if (m.type == LogType.Error || m.type == LogType.Exception) Debug.Log("PROBE_BUILD_MSG " + m.type + " " + step.name + ": " + m.content);
        EditorApplication.Exit(report.summary.result == BuildResult.Succeeded ? 0 : 1);
    }
}
