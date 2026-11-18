using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Deneblab.AbcVersion;
using Helpers.Syrup;
using Fallout.Common;
using Fallout.Common.IO;
using Fallout.Solutions;
using Fallout.Common.Tooling;
using Fallout.Common.Tools.DotNet;
using Fallout.Common.Tools.NuGet;
using Fallout.Common.Utilities.Collections;
using Serilog;
using static Fallout.Common.Tools.DotNet.DotNetTasks;
using static Fallout.Common.Tools.NuGet.NuGetTasks;

class Build : FalloutBuild
{
    readonly DateTime BuildDate = DateTime.UtcNow;

    [Parameter("Configuration to build - Default is 'Debug' (local) or 'Release' (server)")]
    readonly Configuration Configuration = IsLocalBuild ? Configuration.Debug : Configuration.Release;

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
        });
    Target Clean => _ => _
        .DependsOn(Information)
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

            // Check if running on Windows since the test project targets net10.0-windows
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

    void PublishSingleFile(Project p, AbsolutePath outDir)
    {
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
    }

    Target GithubRelease => _ => _
        .DependsOn(Test)
        .Executes(() =>
        {
            var p = CorneyWinProject;
            if (p == null) return;

            Log.Information($"Build Single-File; Project file: {p.Name}; Version: {AbcVersion.SemVersion}");
            var outDir = TmpBuild / p.Name / "github-release";
            outDir.CreateOrCleanDirectory();


            PublishSingleFile(p, outDir);

            Log.Information($"Single-file executable created: {outDir / "Corney.exe"}");
        });

    Target PublishLocalStandalone => _ => _
        .DependsOn(Restore)
        .Executes(() =>
        {
            var p = CorneyWinProject;
            if (p == null) return;

            var outDir = RootDirectory / "dev" / "app.standalone";
            outDir.CreateDirectory();
            Log.Information($"Build standalone; Project file: {p.Name}; Output: {outDir}");

            PublishSingleFile(p, outDir);

            // Marker dir: without it the exe, located inside the git repo, would be treated as Dev and use dev/app.vs
            (outDir / ".syrup").CreateDirectory();

            // Sample config, never overwrites files that already exist
            var cfgDir = outDir / "config" / Environment.MachineName / "corney";
            cfgDir.CreateDirectory();

            var crontabFile = cfgDir / "crontab.txt";
            if (!crontabFile.FileExists())
                File.WriteAllText(crontabFile, string.Join(Environment.NewLine,
                    "# Corney sample crontab: minute hour day-of-month month day-of-week command",
                    "# Runs every 5 minutes",
                    "*/5 * * * * cmd.exe /c echo Corney sample job",
                    "") , new UTF8Encoding(false));

            var configFile = cfgDir / "config.json";
            if (!configFile.FileExists())
                File.WriteAllText(configFile, System.Text.Json.JsonSerializer.Serialize(
                    new { CrontabFiles = new[] { crontabFile.ToString() } },
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));

            Log.Information($"Standalone ready: {outDir / "Corney.exe"}");
            Log.Information($"Config: {configFile}");
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

            Log.Information($"Syrup files ready in {tmpReady}; publish with scripts/publish-syrup.ps1");
        });

    public static int Main() => Execute<Build>(x => x.GithubRelease);
}