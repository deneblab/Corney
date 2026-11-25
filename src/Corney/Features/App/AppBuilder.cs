using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using Corney.Common.Diagnostics;
using Corney.Common.Io;
using Corney.Common.Logging;
using Corney.Common.Performance;
using Corney.Common.SimpleEnv;
using Corney.Features.Cron;
using Corney.Features.Monitors;
using Corney.Features.Processes;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NLog.Config;
using NLog.Extensions.Logging;
using NLog.Targets;

namespace Corney.Features.App;

public class AppBuilder
{
    public async Task Stage1(string[] args)
    {
        var env = SimpleEnv.GetWithAppRootMarkersSafe(".syrup");
        try
        {
            var config = await CreateConfigAsync(env);
            var registry = CreateRegistry(env, config);
            var host = CreateHost(args, registry);
            await Stage2(host);
        }
        catch (Exception e)
        {
            var l = new List<string>
            {
                e.Message,
                e.StackTrace,
                $"Env: {JsonSerializer.Serialize(env,
                    new JsonSerializerOptions { WriteIndented = true })}"
            };
            await WriteStartupErrorAsync(env, l);
        }
        finally
        {
            NLog.LogManager.Shutdown();
        }
    }

    // error.log.txt goes to the log dir; when that fails, next to the exe, so a startup error is never lost
    private static async Task WriteStartupErrorAsync(SimpleEnvResult env, List<string> lines)
    {
        if (!string.IsNullOrEmpty(env?.LogDir))
        {
            try
            {
                Directory.CreateDirectory(env.LogDir);
                await File.WriteAllLinesAsync(Path.Combine(env.LogDir, "error.log.txt"), lines);
                return;
            }
            catch (Exception)
            {
                // fall back to the exe dir
            }
        }

        var dir = string.IsNullOrEmpty(Environment.ProcessPath)
            ? Environment.CurrentDirectory
            : Path.GetDirectoryName(Environment.ProcessPath);
        await File.WriteAllLinesAsync(Path.Combine(dir!, "error.log.txt"), lines);
    }

    private async Task Stage2(IHost host)
    {
        var log = host.Services.GetRequiredService<ILogger<AppBuilder>>();
        var registry = host.Services.GetRequiredService<CorneyRegistry>();
        var env = registry.AppEnv;
        try
        {
            await Process(host, log, registry);
        }
        catch (Exception e)
        {
            log.LogCritical(e.Message);
            log.LogCritical(e.StackTrace);
            log.LogCritical($"Env: {JsonSerializer.Serialize(env,
                new JsonSerializerOptions { WriteIndented = true })}");
        }
    }

    private IHost CreateHost(string[] args, CorneyRegistry registry)
    {
        var host = Host.CreateDefaultBuilder(args)
            // Optionally, register additional services such as your WinForms main form.
            .ConfigureServices(services => ConfigureServices(services, registry))
            .ConfigureLogging(l =>
            {
                l.ClearProviders();
                l.SetMinimumLevel(LogLevel.Debug);
                l.AddNLog(CreateLogConfiguration(registry.AppEnv.LogDir));
            })
            .Build();

        return host;
    }

    internal static void ConfigureServices(IServiceCollection services, CorneyRegistry registry)
    {
        services.AddCorneyCore(registry);

        // Windows UI: tray context and config file menu
        services.AddSingleton<CorneyContext>();
        services.AddTransient<IFileOperationHelper, FileOperationHelper>();
        services.AddTransient<IConfigFileMenuService, ConfigFileMenuService>();
    }

    private static LoggingConfiguration CreateLogConfiguration(string logDir)
    {
        var config = new LoggingConfiguration();
        var file = new FileTarget("file")
        {
            FileName = Path.Combine(logDir, "corney.${date:format=yyyy-MM-dd}.txt"),
            Layout = "${date:format=yyyy-MM-dd HH\\:mm\\:ss.fff} ${level:uppercase=true:padding=-5} " +
                     "${logger:shortName=true} ${message} ${exception:format=tostring}",
            MaxArchiveFiles = 9, // plus the current file = last 10
            KeepFileOpen = false,
            CreateDirs = true
        };
        config.AddRule(NLog.LogLevel.Debug, NLog.LogLevel.Fatal, file);
        return config;
    }

    private async Task<CorneyConfig> CreateConfigAsync(SimpleEnvResult env)
    {
        var configPath = ConfigPath(env);
        CorneyConfig config;

        if (File.Exists(configPath))
        {
            // _log.Trace($"Using existing config file at {configPath}");
            config = await Misc.ReadJsonAsync<CorneyConfig>(configPath);
        }
        else
        {
            // _log.Trace($"Creating new config file at {configPath}");
            config = new CorneyConfig();
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            await Misc.WriteJsonAsync(configPath, config);
        }

        // Validate configuration
        var validationResult = ConfigurationValidator.ValidateConfiguration(config);

        if (!validationResult.IsValid)
        {
            var errorMessage = validationResult.GetFormattedErrorMessage();
            // _log.LogError("Configuration validation failed:{NewLine}{ErrorMessage}", Environment.NewLine, errorMessage);
            throw new InvalidOperationException($"Configuration validation failed:{Environment.NewLine}{errorMessage}");
        }

        // Log warnings if any
        //if (validationResult.Warnings.Count > 0)
        //    foreach (var warning in validationResult.Warnings)
        //        _log.LogWarning("Configuration warning - {PropertyName}: {WarningMessage}",
        //            warning.PropertyName, warning.WarningMessage);

        return config;
    }

    [SupportedOSPlatform("windows6.1")]
    private static async Task Process(IHost host, ILogger log, CorneyRegistry registry)
    {
        log.LogInformation(LogMessages.ApplicationStarted, LogMessages.ApplicationStartedTemplate,
            $"{registry.AppVersion.Product} {registry.AppVersion.SemVer}");
        log.LogInformation($"Mode: {registry.AppEnv.AppMode}; " +
                           $"Root: {registry.AppEnv.AppRoot};");

        var mediator = host.Services.GetRequiredService<IMediator>();

        await mediator.Publish(new AppStartingEvent());
        await mediator.Publish(new StartCorneyReq(registry.CrontabFiles));

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        var form = host.Services.GetRequiredService<CorneyContext>();
        Application.Run(form);
    }

    private string ConfigPath(SimpleEnvResult env)
    {
        var configPath = Path.Combine(env.ConfigDir, Environment.MachineName, "corney", "config.json");
        return configPath;
    }

    private CorneyRegistry CreateRegistry(SimpleEnvResult env, CorneyConfig config)
    {
        var configPath = ConfigPath(env);
        return new CorneyRegistry(env, configPath, config);
    }
}