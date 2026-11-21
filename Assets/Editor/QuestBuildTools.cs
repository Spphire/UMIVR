using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;

public static class QuestBuildTools
{
    private const string ScenePath = "Assets/Scenes/QuestUMIVR_v2_fileupload.unity";
    private const string OutputPath = "Build/questumivr_fileupload/QuestUMIVR_v2_fileupload.apk";

    public static void BuildQuestUMIVRFileUploadApk()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(OutputPath) ?? "Build/questumivr_fileupload");

        EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);

        var options = new BuildPlayerOptions
        {
            scenes = new[] { ScenePath },
            locationPathName = OutputPath,
            target = BuildTarget.Android,
            options = BuildOptions.None
        };

        BuildReport report = BuildPipeline.BuildPlayer(options);
        BuildSummary summary = report.summary;

        if (summary.result != BuildResult.Succeeded)
            throw new Exception($"Android build failed: {summary.result}");

        Console.WriteLine($"Android build succeeded: {summary.outputPath}");
    }
}
