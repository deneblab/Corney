using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Corney.Features.Processes;
using Corney.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Corney.Tests.Processes;

/// <summary>
///     CORNEY-008 on real Windows: shortcuts and batch files started the way Corney starts crontab jobs.
///     Each test gets a folder (with a space in its name) holding target.cmd, which appends what it
///     receives to result.txt, and test.lnk pointing to it.
/// </summary>
public sealed class ProcessWrapperWindowsTests : IDisposable
{
    // Set by scripts/test-lnk.ps1 to test a shortcut created outside the test.
    private const string ShortcutEnvVar = "CORNEY_TEST_LNK";

    // Same shape as the SyrupEvolve crontab line, plus a quoted argument with a space.
    private const string Arguments = @"update -p simple-scraper,robe-nova ""a b""";

    private readonly string _dir;

    public ProcessWrapperWindowsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "corney tests " + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [WindowsFact]
    public void Start_Shortcut_RunsTargetWithArguments()
    {
        var shortcut = Environment.GetEnvironmentVariable(ShortcutEnvVar);
        if (string.IsNullOrEmpty(shortcut))
            shortcut = CreateShortcut(Path.Combine(_dir, "test.lnk"), CreateTargetCmd());
        var result = Path.Combine(Path.GetDirectoryName(shortcut)!, "result.txt");
        if (File.Exists(result))
            File.Delete(result);

        var text = StartAndWait($"\"{shortcut}\" {Arguments}", result);

        Assert.Contains("args=" + Arguments, text);
    }

    [WindowsFact]
    public void Start_BatchFile_RunsDirectlyWithArguments()
    {
        var target = CreateTargetCmd();
        var result = Path.Combine(_dir, "result.txt");

        var text = StartAndWait($"\"{target}\" {Arguments}", result);

        Assert.Contains("args=" + Arguments, text);
        Assert.Contains("cwd=" + _dir, text);
    }

    private static string StartAndWait(string crontabCommand, string result)
    {
        var item = Pharse.Tokenize2(crontabCommand);

        var stopwatch = Stopwatch.StartNew();
        new ProcessWrapper(NullLogger<ProcessWrapper>.Instance).Start(item);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Start took {stopwatch.Elapsed}");

        return WaitForText(result, "args=", TimeSpan.FromSeconds(15));
    }

    private string CreateTargetCmd()
    {
        var target = Path.Combine(_dir, "target.cmd");
        File.WriteAllLines(target, new[]
        {
            "@echo off",
            @">> ""%~dp0result.txt"" echo cwd=%cd%",
            @">> ""%~dp0result.txt"" echo args=%*"
        });
        return target;
    }

    private static string CreateShortcut(string shortcutPath, string targetPath)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
                        ?? throw new InvalidOperationException("WScript.Shell is not available");
        var shell = Activator.CreateInstance(shellType)!;
        object link = null;
        try
        {
            link = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell,
                new object[] { shortcutPath })!;
            var linkType = link.GetType();
            linkType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, link, new object[] { targetPath });
            linkType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, link,
                new object[] { Path.GetDirectoryName(targetPath) });
            linkType.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
        }
        finally
        {
            if (link != null)
                Marshal.ReleaseComObject(link);
            Marshal.ReleaseComObject(shell);
        }

        return shortcutPath;
    }

    private static string WaitForText(string path, string expected, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var text = string.Empty;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (File.Exists(path))
                {
                    text = File.ReadAllText(path);
                    if (text.Contains(expected))
                        return text;
                }
            }
            catch (IOException)
            {
                // target.cmd is still writing
            }

            Thread.Sleep(200);
        }

        return text;
    }
}
