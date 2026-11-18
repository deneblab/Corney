using System;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Corney.Features.App;

namespace Corney;

internal class App
{
    /// <summary>
    ///     The main entry point for the application.
    /// </summary>
    [STAThread]
    [SupportedOSPlatform("windows6.1")]
    public static async Task Main(string[] args)
    {
        const string mutexName = "Global\\Corney_SingleInstance";
        using var mutex = new Mutex(true, mutexName, out var createdNew);
        if (!createdNew) return;
        var ab = new AppBuilder();
        await ab.Stage1(args);
    }
}