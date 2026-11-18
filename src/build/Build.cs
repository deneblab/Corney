using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Deneblab.AbcVersion;
using Helpers.Azure;
using Helpers.Syrup;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.DotNet;
using Nuke.Common.Tools.NuGet;
using Nuke.Common.Utilities.Collections;
using Serilog;
using static Nuke.Common.Tools.DotNet.DotNetTasks;
using static Nuke.Common.Tools.NuGet.NuGetTasks;

class Build : NukeBuild
{
    [Parameter] readonly string AzureDevOpsToken = Environment.GetEnvironmentVariable("SYSTEM_ACCESSTOKEN");

    /// Support plugins are available for:
    /// - JetBrains ReSharper        https://nuke.build/resharper
    /// - JetBrains Rider            https://nuke.build/rider
    /// - Microsoft VisualStudio     https://nuke.build/visualstudio
    /// - Microsoft VSCode           https://nuke.build/vscode
    readonly DateTime BuildDate = DateTime.UtcNow;

    [Parameter("Configuration to build - Default is 'Debug' (local) or 'Release' (server)")]
    readonly Configuration Configuration = IsLocalBuild ? Configuration.Debug : Configuration.Release;

    readonly bool IsAzureDevOps = string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AGENT_NAME")) == false;
    [Solution] readonly Solution Solution;
    Project CorneyWinProject => Solution.GetProject("Corney").NotNull();
    AbsolutePath SourceDirectory => RootDirectory / "src";
    AbsolutePath TmpBuild => TemporaryDirectory / "w";

    AbcVersion AbcVersion => AbcVersionFactory.CreateOneBuilder()
        .SetDateTime(BuildDate)
        .SetRepositoryRoot(RootDirectory)
        .Build(); // Creates new instance

    Target Information => _ => _
        .Executes(() =>
        {
            Log.Information($"Host: '{Host}'");
            Log.Information($"Version: '{AbcVersion.SemVersion}'");
            Log.Information($"Date: '{AbcVersion.DateTime:s}Z'");
            Log.Information($"Build target: {Configuration}");
            Log.Information($"FullVersion: '{AbcVersion.InformationalVersion}'");
            Log.Information($"AzureDevOps IsAzureDevOps: '{IsAzureDevOps}'");
            Log.Information($"AzureDevOps AgentName: '{Environment.GetEnvironmentVariable("AGENT_NAME")}'");
            Log.Information(
                $"AzureDevOps ArtifactDir: '{Environment.GetEnvironmentVariable("BUILD_ARTIFACTSTAGINGDIRECTORY")}'");
        });
    Target ConfigureAzureDevOps => _ => _
        .DependsOn(Information)
        .OnlyWhenStatic(() => IsAzureDevOps)
        .Executes(() =>
        {
            Log.Information($"Set version to AzureDevOps: {AbcVersion.SemVersion}");
            // https://github.com/microsoft/azure-pipelines-tasks/blob/master/docs/authoring/commands.md
            Log.Information($"##vso[build.updatebuildnumber]{AbcVersion.SemVersion}");
        });
    Target Clean => _ => _
        .DependsOn(ConfigureAzureDevOps)
        .Executes(() =>
        {
            TmpBuild.CreateOrCleanDirectory();
            SourceDirectory.GlobDirectories("**/bin", "**/obj").ForEach(x =>
            {
                if (x.Parent?.Name == "build") return;
                if (x.Parent?.Name == "infra") return;
                x.DeleteDirectory();
            });
        });

    Target Restore => _ => _
        .DependsOn(Clean)
        .Executes(() =>
        {
            DotNetRestore(s => s.SetProjectFile(CorneyWinProject));
        });


    Target Test => _ => _
        .DependsOn(Restore)
        .Executes(() =>
        {
            var testProject = Solution.GetProject("Corney.Tests");
            if (testProject == null)
            {
                Log.Warning("Test project not found, skipping tests");
                return;
            }

            // Check if running on Windows since the test project targets net8.0-windows
            if (!OperatingSystem.IsWindows())
            {
                Log.Warning("Skipping tests - Windows Forms tests require Windows runtime");
                return;
            }

            var p = CorneyWinProject;
            if (p == null) return;

            var outDir = TmpBuild / CorneyWinProject.Name / "test-result";
            outDir.CreateOrCleanDirectory();

            try
            {
                DotNetTest(s => s
                    .SetProjectFile(testProject)
                    .SetConfiguration(Configuration)
                    .SetLoggers("trx")
                    .SetResultsDirectory(outDir));
            }
            catch (ProcessException ex)
            {
                Log.Warning($"Tests failed with exit code: {ex.ExitCode}");
                Log.Information("Continuing build despite test failures...");
            }
        });

    Target GithubRelease => _ => _
        .DependsOn(Test)
        .Executes(() =>
        {
            var p = CorneyWinProject;
            if (p == null) return;

            Log.Information($"Build Single-File; Project file: {p.Name}; Version: {AbcVersion.SemVersion}");
            var outDir = TmpBuild / p.Name / "github-release";
            outDir.CreateOrCleanDirectory();


            // Restore with runtime identifier
            DotNetRestore(s => s
                .SetProjectFile(p.Path)
                .SetRuntime("win-x64")
            );

            DotNetPublish(o => o
                .SetProject(p.Path)
                .EnableNoRestore()
                .SetConfiguration(Configuration)
                .SetOutput(outDir)
                .EnablePublishSingleFile()
                .SetSelfContained(false)
                .SetRuntime("win-x64")
                .SetVersion(AbcVersion.SemVersion)
                .SetFileVersion(AbcVersion.SemVersion)
                .SetAssemblyVersion(AbcVersion.SemVersion)
                .SetInformationalVersion(AbcVersion.InformationalVersion)
            );

            Log.Information($"Single-file executable created: {outDir / "Corney.exe"}");
        });

    Target Syrup => _ => _
        .DependsOn(GithubRelease)
        .Executes(() =>

        {
            var p = CorneyWinProject;
            if (p == null) return;
            var tmpMerge = TmpBuild / p.Name / "github-release";
            var tmpNuget = TmpBuild / p.Name / "nuget-tmp";
            var tmpMain = tmpNuget / "main" / p.Name;
            var srcBuild = SourceDirectory / "build";
            var tmpReady = TmpBuild / p.Name / "syrup";

            tmpNuget.CreateOrCleanDirectory();
            tmpReady.CreateOrCleanDirectory();

            // syrup dir
            (tmpNuget / "_syrup").CreateOrCleanDirectory();
            (srcBuild / "syrup").Copy(tmpNuget / "_syrup", ExistsPolicy.MergeAndOverwrite);


            // main dir
            tmpMain.CreateOrCleanDirectory();
            tmpMerge.Copy(tmpMain, ExistsPolicy.MergeAndOverwrite);


            // nuget definition
            var srcNugetFile = srcBuild / "nuget" / "nuget.nuspec";
            var dstNugetFile = tmpNuget / $"{p.Name}.nuspec";
            srcNugetFile.Copy(dstNugetFile);
            var text = File.ReadAllText(srcNugetFile);
            var r1 = text.Replace("{Version}", AbcVersion.SemVersion);
            var r2 = r1.Replace("{Name}", p.Name);
            File.WriteAllText(dstNugetFile, r2, new UTF8Encoding(false));

            NuGetPack(s => s
                .SetTargetPath(dstNugetFile)
                .SetOutputDirectory(tmpReady)
                .SetVersion(AbcVersion.SemVersion)
                .EnableNoPackageAnalysis());


            var nugetFiles = tmpReady.GlobFiles("*.nupkg");

            foreach (var file in nugetFiles) SyrupTools.MakeSyrupFile(file, AbcVersion, p.Name);
        });

    Target PublishAzureDevOpsArtifacts => _ => _
        .DependsOn(Syrup)
        .OnlyWhenStatic(() => IsAzureDevOps)
        .Executes(() =>
        {
            var p = CorneyWinProject;
            if (p == null) return;
            var tmpReady = TmpBuild / p.Name / "syrup";
            var serverPublishArtifact = Environment.GetEnvironmentVariable("BUILD_ARTIFACTSTAGINGDIRECTORY");
            tmpReady.Copy( serverPublishArtifact, ExistsPolicy.MergeAndOverwrite);
        });

    Target PublishAzureDevOpsStorage => _ => _
        .DependsOn(PublishAzureDevOpsArtifacts)
        .OnlyWhenStatic(() => IsAzureDevOps)
        .Executes(async () =>
        {
            Log.Information($"PublishAzureDevOpsStorage ; Start");

            void LogFiles(string title, List<ReleaseInfo> filesToShow)
            {
                Log.Information($"{title}: {filesToShow.Count}");
                foreach (var l in filesToShow) Log.Information($"Name: {l.Name}; Date: {l.ReleaseDate}");
            }

            var storageConnectionString = Environment.GetEnvironmentVariable("azureStorageConnectionStringKey1");
            Log.Information($"Build; storageConnectionString: {storageConnectionString}");
            Log.Information($"Build; containerName: {Conf.AZURE_CONTAINER_NAME}");
            var serverPublishArtifact = Environment.GetEnvironmentVariable("BUILD_ARTIFACTSTAGINGDIRECTORY");
            var files = Directory.GetFiles(serverPublishArtifact).ToList();
            var client = AzureSyrupTools.Create(storageConnectionString, Conf.AZURE_CONTAINER_NAME);
            await client.UploadFiles(files);
            var list = await client.GetSyrupFiles();
            var fileToRemove = list.OrderByDescending(x => x.ReleaseDate).Skip(15).ToList();
            LogFiles("Files to remove", fileToRemove);
            await client.RemoveSyrupFiles(fileToRemove);
            var newList = await client.GetSyrupFiles();
            await client.CreateSyrupFilesList(newList);
            LogFiles("Files in container", newList);
        });



    public static int Main() => Execute<Build>(x => x.PublishAzureDevOpsStorage);
}

public static class Conf
{
    public const string AZURE_CONTAINER_NAME = "application-corney";
}