using EnrollmentService.Metrics;
using EnrollmentService.Queues;
using EnrollmentService.Repositories;
using EnrollmentService.Services;
using EnrollmentService.Services.Processing;

namespace EnrollmentService.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructureServices(
        this IServiceCollection services,
        IConfiguration configuration,
        out ServiceOptions serviceOptions)
    {
        serviceOptions = ServiceOptions.FromConfiguration(configuration);
        services.AddSingleton(serviceOptions);

        var opts = serviceOptions;
        services.AddSingleton<IBoundedRequestQueue>(_ =>
        {
            if (string.Equals(opts.QueueEngine, "system_channel", StringComparison.OrdinalIgnoreCase))
            {
                return new ChannelRequestQueue(opts.QueueCapacity);
            }
            return new MonitorRequestQueue(opts.QueueCapacity);
        });

        services.AddSingleton<DatabaseInitializer>();
        services.AddSingleton<IDbConnectionFactory, NpgsqlConnectionFactory>();

        return services;
    }

    public static IServiceCollection AddRepositories(this IServiceCollection services)
    {
        services.AddSingleton<GroupRepository>();
        services.AddSingleton<EnrollmentRepository>();
        services.AddSingleton<EnrollmentRequestRepository>();
        services.AddSingleton<DecanateRepository>();
        return services;
    }

    public static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        services.AddSingleton<EnrollmentService.Services.EnrollmentService>();
        return services;
    }

    public static IServiceCollection AddWorkerProcessing(
        this IServiceCollection services,
        bool migrateOnly)
    {
        services.AddSingleton<EnrollmentRequestProcessor>();
        services.AddSingleton<EnrollmentWorkerPool>();

        if (!migrateOnly)
        {
            services.AddHostedService(sp => sp.GetRequiredService<EnrollmentWorkerPool>());
            services.AddHostedService<HeldEnrollmentExpirationBackgroundService>();
        }

        return services;
    }

    public static IServiceCollection AddObservability(this IServiceCollection services)
    {
        services.AddSingleton<MetricsCollector>();
        return services;
    }
}
