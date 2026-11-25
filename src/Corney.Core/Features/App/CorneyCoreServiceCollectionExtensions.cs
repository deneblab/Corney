using System;
using Corney.Common.Diagnostics;
using Corney.Common.Performance;
using Corney.Features.Cron;
using Corney.Features.Monitors;
using Corney.Features.Processes;
using Microsoft.Extensions.DependencyInjection;

namespace Corney.Features.App;

public static class CorneyCoreServiceCollectionExtensions
{
    /// <summary>
    ///     Registers the platform-independent Corney services: scheduler, process execution,
    ///     crontab monitoring, performance monitoring and MediatR handlers from Corney.Core.
    /// </summary>
    public static IServiceCollection AddCorneyCore(this IServiceCollection services, CorneyRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        services.AddSingleton(registry);
        services.AddSingleton<ICronService, CronService>();
        services.AddTransient<ProcessWrapper>();
        services.AddHostedService<MinuteBackgroundService>();
        services.AddSingleton<ConfigFileMonitorService>();
        services.AddSingleton<FileWatchHelpers>();
        services.AddSingleton<CrontabFileParser>();

        services.AddPerformanceMonitoring(options =>
        {
            options.ReportingInterval = TimeSpan.FromMinutes(15);
            options.DashboardInterval = TimeSpan.FromHours(1);
            options.EnableAutomaticReporting = true;
        });

        services.AddSingleton<DevelopmentValidator>();
        services.AddTransient(typeof(EnhancedLogger<>));

        services.AddMediatR(cfg => { cfg.RegisterServicesFromAssembly(typeof(CronService).Assembly); });

        return services;
    }
}
