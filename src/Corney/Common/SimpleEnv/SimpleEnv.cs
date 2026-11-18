namespace Corney.Common.SimpleEnv;

using System;
using System.IO;

public enum SimpleAppMode
{
    Dev = 20,
    Test = 30,
    Docker = 400,
    ProcessPathDir = 500,
    CurrentUserDir = 600,
    UserRoot = 1000,
    CustomRoot = 1100
}

public class SimpleEnvResult
{
    public SimpleAppMode AppMode { get; set; }
    public string AppRoot { get; set; }
    public string ConfigDir { get; set; }
    public string LogDir { get; set; }
}

public static class SimpleEnv
{

    public static SimpleEnvResult GetWithCustomRoot(string customRoot)
    {
        if (string.IsNullOrWhiteSpace(customRoot))
        {
            throw new ArgumentException("Custom root cannot be null or whitespace", nameof(customRoot));
        }

        return new SimpleEnvResult
        {
            AppMode = SimpleAppMode.CustomRoot,
            AppRoot = customRoot,
            ConfigDir = Path.Combine(customRoot, "config"),
            LogDir = Path.Combine(customRoot, "log")
        };
    }

    public static SimpleEnvResult GetWithAppRootMarkers(params string[] directoryNames)
    {
        return GetWithAppRootMarkersInternal(true, directoryNames);
    }

    public static SimpleEnvResult GetWithAppRootMarkersSafe(params string[] directoryNames)
    {
        return GetWithAppRootMarkersInternal(false, directoryNames);
    }

    private static SimpleEnvResult GetWithAppRootMarkersInternal(bool throwIfNotDetected, params string[] directoryNames)
    {
        if (directoryNames?.Length == 0)
        {
            if (throwIfNotDetected)
            {
                throw new ArgumentException("At least one directory marker must be provided", nameof(directoryNames));
            }
            // Fall back to regular detection if no markers provided
            return Detect();
        }

        var markers = directoryNames ?? throw new ArgumentNullException(nameof(directoryNames));

        var mode = DetermineAppMode(SimpleAppMode.ProcessPathDir);
        var root = DetermineAppRoot(mode);

        // Check for UserRoot with custom markers
        if (IsUserRootWithMarkers(markers))
        {
            var userRoot = FindUserRootWithMarkers(markers);
            return new SimpleEnvResult
            {
                AppMode = SimpleAppMode.UserRoot,
                AppRoot = userRoot,
                ConfigDir = Path.Combine(userRoot, "config"),
                LogDir = Path.Combine(userRoot, "log")
            };
        }

        // If not UserRoot and throwIfNotDetected is true, throw
        if (throwIfNotDetected)
        {
            var markerList = string.Join(", ", markers);
            throw new InvalidOperationException($"No app root markers [{markerList}] found in directory hierarchy");
        }

        // Fallback to regular detection
        return new SimpleEnvResult
        {
            AppMode = mode,
            AppRoot = root,
            ConfigDir = Path.Combine(root, "config"),
            LogDir = Path.Combine(root, "log")
        };
    }

    private static void ValidateFallbackMode(SimpleAppMode fallbackMode)
    {
        if (fallbackMode != SimpleAppMode.CurrentUserDir &&
            fallbackMode != SimpleAppMode.ProcessPathDir)
        {
            throw new ArgumentException(
                "Fallback mode must be CurrentUserDir or ProcessPathDir",
                nameof(fallbackMode));
        }
    }

    public static SimpleEnvResult Detect()
    {
        return Detect(SimpleAppMode.ProcessPathDir);
    }

    public static SimpleEnvResult Detect(SimpleAppMode fallbackMode)
    {
        ValidateFallbackMode(fallbackMode);

        var mode = DetermineAppMode(fallbackMode);
        var root = DetermineAppRoot(mode);

        return new SimpleEnvResult
        {
            AppMode = mode,
            AppRoot = root,
            ConfigDir = Path.Combine(root, "config"),
            LogDir = Path.Combine(root, "log")
        };
    }

    private static SimpleAppMode DetermineAppMode(SimpleAppMode fallbackMode)
    {
        if (IsDocker()) return SimpleAppMode.Docker;
        if (IsTest()) return SimpleAppMode.Test;
        if (IsDev()) return SimpleAppMode.Dev;
        return fallbackMode;
    }

    private static bool IsDocker()
    {
        return File.Exists("/.dockerenv") ||
               Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true";
    }


    private static bool IsTest()
    {
        var commandLine = Environment.CommandLine;
        return commandLine.Contains("testhost") ||
               commandLine.Contains("vstest") ||
               commandLine.Contains("dotnet test") ||
               commandLine.Contains("ReSharperTestRunner")
               ;
    }

    private static bool IsDev()
    {
        return FindGitRoot() != null;
    }

    private static string DetermineAppRoot(SimpleAppMode mode)
    {
        return mode switch
        {
            SimpleAppMode.Docker => "/home/app",
            SimpleAppMode.Test => FindTestRoot(),
            SimpleAppMode.Dev => FindDevRoot(),
            SimpleAppMode.CurrentUserDir => Directory.GetCurrentDirectory(),
            SimpleAppMode.ProcessPathDir => Path.GetDirectoryName(Environment.ProcessPath) ?? Directory.GetCurrentDirectory(),
            _ => Directory.GetCurrentDirectory()
        };
    }


    private static string FindGitRoot()
    {
        // Check from Environment.ProcessPath
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(processPath))
        {
            var processDir = Path.GetDirectoryName(processPath);
            if (!string.IsNullOrEmpty(processDir))
            {
                var processResult = FindGitInPath(processDir);
                if (processResult != null) return processResult;
            }
        }

        // Check from EntryAssembly location
        var entryAssembly = System.Reflection.Assembly.GetEntryAssembly();
        if (entryAssembly?.Location != null)
        {
            var assemblyDir = Path.GetDirectoryName(entryAssembly.Location);
            if (!string.IsNullOrEmpty(assemblyDir))
            {
                var assemblyResult = FindGitInPath(assemblyDir);
                if (assemblyResult != null) return assemblyResult;
            }
        }

        // Check from current working directory (fallback for test scenarios)
        var currentDir = Directory.GetCurrentDirectory();
        var currentResult = FindGitInPath(currentDir);
        if (currentResult != null) return currentResult;

        return null;
    }

    private static string FindGitInPath(string startPath)
    {
        var current = new DirectoryInfo(startPath);
        while (current != null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, ".git")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }
        return null;
    }

    private static string FindDevRoot()
    {
        var gitRoot = FindGitRoot();
        if (gitRoot == null) return Directory.GetCurrentDirectory();

        return Path.Combine(gitRoot, "dev", "app.vs");
    }

    private static string FindTestRoot()
    {
        var gitRoot = FindGitRoot();
        if (gitRoot != null)
        {
            return Path.Combine(gitRoot, "dev", "app.vs");
        }

        return Path.Combine(Directory.GetCurrentDirectory(), "test-output");
    }

    private static bool IsUserRootWithMarkers(string[] markers)
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(processPath)) return false;
        var processDir = Path.GetDirectoryName(processPath);
        if (string.IsNullOrEmpty(processDir)) return false;

        try
        {
            foreach (var dirName in markers)
            {
                if (Directory.GetDirectories(processDir, dirName).Length > 0)
                {
                    return true;
                }
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static string FindUserRootWithMarkers(string[] markers)
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(processPath)) return Directory.GetCurrentDirectory();

        var processDir = Path.GetDirectoryName(processPath);
        if (string.IsNullOrEmpty(processDir)) return Directory.GetCurrentDirectory();

        var current = new DirectoryInfo(processDir);
        while (current != null)
        {
            foreach (var dirName in markers)
            {
                var markerDirs = current.GetDirectories(dirName);
                if (markerDirs.Length > 0)
                {
                    return current.FullName;
                }
            }
            current = current.Parent;
        }

        return Directory.GetCurrentDirectory();
    }
}