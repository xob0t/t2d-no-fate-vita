using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

// Batch-mode build of the importer reference (tools/downgrade/audio/importer-reference/make.py):
//   Unity.exe -batchmode -nographics -buildTarget PSP2 -projectPath <project> -executeMethod ImporterReference.Build
//     -bundleOutput <folder>
// Every clip under Assets/probe goes into one uncompressed asset bundle for the PS Vita, with the import settings of
// its .meta file; make.py reads the bundle's AudioClips and FSB5 banks back. A bundle needs no player build, so the
// reference takes a minute instead of an IL2CPP build. Logs each clip's PS Vita import (IMPORTREF_CLIP).
public static class ImporterReference
{
    public static void Build()
    {
        string[] args = Environment.GetCommandLineArgs();
        int at = Array.IndexOf(args, "-bundleOutput");
        if (at < 0 || at + 1 >= args.Length) throw new ArgumentException("-bundleOutput needed");
        string output = args[at + 1];
        if (!EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.PSP2, BuildTarget.PSP2))
        {
            Debug.LogError("IMPORTREF_BUILD Failed: no PS Vita build support");
            EditorApplication.Exit(2);
            return;
        }
        AssetDatabase.Refresh();
        string[] paths = AssetDatabase.FindAssets("t:AudioClip", new[] { "Assets/probe" })
            .Select(g => AssetDatabase.GUIDToAssetPath(g)).OrderBy(p => p, StringComparer.Ordinal).ToArray();
        foreach (string path in paths)
        {
            var importer = (AudioImporter)AssetImporter.GetAtPath(path);
            var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
            // 2018.2's AudioImporter has no public normalize; the .meta sets it.
            Debug.Log("IMPORTREF_CLIP " + path + " mono=" + importer.forceToMono +
                      " clip=" + (clip == null ? "none" : clip.frequency + "Hz " + clip.channels + "ch " + clip.samples + " samples"));
        }
        Directory.CreateDirectory(output);
        var build = new AssetBundleBuild { assetBundleName = "probes", assetNames = paths };
        AssetBundleManifest manifest = BuildPipeline.BuildAssetBundles(output, new[] { build },
            BuildAssetBundleOptions.UncompressedAssetBundle, BuildTarget.PSP2);
        Debug.Log("IMPORTREF_BUILD " + (manifest != null ? "Succeeded" : "Failed") + " clips=" + paths.Length);
        EditorApplication.Exit(manifest != null ? 0 : 1);
    }
}
