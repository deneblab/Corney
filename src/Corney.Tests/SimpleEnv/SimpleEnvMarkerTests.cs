using System;
using System.IO;
using Xunit;

namespace Corney.Tests.SimpleEnv;

public class SimpleEnvMarkerTests : IDisposable
{
    private readonly string _tmp;

    public SimpleEnvMarkerTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "corney-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void FindRootWithMarkers_MarkerNextToExe_ReturnsExeDir()
    {
        // Arrange (standalone layout)
        var exeDir = Directory.CreateDirectory(Path.Combine(_tmp, "app.standalone")).FullName;
        Directory.CreateDirectory(Path.Combine(exeDir, ".syrup"));

        // Act
        var root = Common.SimpleEnv.SimpleEnv.FindRootWithMarkers(exeDir, ".syrup");

        // Assert
        Assert.Equal(exeDir, root);
    }

    [Fact]
    public void FindRootWithMarkers_SyrupLayout_ReturnsInstallRoot()
    {
        // Arrange (Syrup layout: <root>\.syrup, exe in <root>\app\Corney\main\Corney)
        var installRoot = Directory.CreateDirectory(Path.Combine(_tmp, "!Syrup", "Corney")).FullName;
        Directory.CreateDirectory(Path.Combine(installRoot, ".syrup"));
        var exeDir = Directory.CreateDirectory(Path.Combine(installRoot, "app", "Corney", "main", "Corney")).FullName;

        // Act
        var root = Common.SimpleEnv.SimpleEnv.FindRootWithMarkers(exeDir, ".syrup");

        // Assert
        Assert.Equal(installRoot, root);
    }

    [Fact]
    public void FindRootWithMarkers_NoMarker_ReturnsNull()
    {
        // Arrange
        var exeDir = Directory.CreateDirectory(Path.Combine(_tmp, "a", "b")).FullName;

        // Act
        var root = Common.SimpleEnv.SimpleEnv.FindRootWithMarkers(exeDir, ".corney-test-marker-" + Guid.NewGuid().ToString("N"));

        // Assert
        Assert.Null(root);
    }

    [Fact]
    public void FindRootWithMarkers_SeveralMarkers_NearestWins()
    {
        // Arrange
        Directory.CreateDirectory(Path.Combine(_tmp, ".syrup"));
        var inner = Directory.CreateDirectory(Path.Combine(_tmp, "inner")).FullName;
        Directory.CreateDirectory(Path.Combine(inner, ".syrup"));
        var exeDir = Directory.CreateDirectory(Path.Combine(inner, "app", "x")).FullName;

        // Act
        var root = Common.SimpleEnv.SimpleEnv.FindRootWithMarkers(exeDir, ".syrup");

        // Assert
        Assert.Equal(inner, root);
    }

    [Fact]
    public void FindRootWithMarkers_FileNamedLikeMarker_IsIgnored()
    {
        // Arrange
        var marker = ".corney-test-marker-" + Guid.NewGuid().ToString("N");
        var exeDir = Directory.CreateDirectory(Path.Combine(_tmp, "app")).FullName;
        File.WriteAllText(Path.Combine(exeDir, marker), "");

        // Act
        var root = Common.SimpleEnv.SimpleEnv.FindRootWithMarkers(exeDir, marker);

        // Assert
        Assert.Null(root);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void FindRootWithMarkers_EmptyStartDir_ReturnsNull(string startDir)
    {
        Assert.Null(Common.SimpleEnv.SimpleEnv.FindRootWithMarkers(startDir, ".syrup"));
    }
}
