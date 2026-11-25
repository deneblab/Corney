using System;
using Xunit;

namespace Corney.Tests.TestSupport;

/// <summary>
///     A fact that runs only on Windows (CI: windows-latest) and is skipped elsewhere.
/// </summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "Windows only";
    }
}
