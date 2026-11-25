using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Corney.Common.Performance;

public static class PerformanceServiceExtensions
{
    /// <summary>
    ///     Registers the PerformanceMonitor singleton (hourly report, resource and failure warnings).
    /// </summary>
    public static IServiceCollection AddPerformanceMonitoring(this IServiceCollection services)
    {
        services.AddSingleton(serviceProvider =>
            new PerformanceMonitor(serviceProvider.GetRequiredService<ILogger<PerformanceMonitor>>()));
        return services;
    }
}
