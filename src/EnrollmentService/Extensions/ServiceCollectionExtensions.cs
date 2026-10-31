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
        services.AddSingleton<IGroupRepository, GroupRepository>();
        services.AddSingleton<IEnrollmentRepository, EnrollmentRepository>();
        services.AddSingleton<IEnrollmentRequestRepository, EnrollmentRequestRepository>();
        services.AddSingleton<IDecanateRepository, DecanateRepository>();
        return services;
    }

    public static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        services.AddSingleton<IEnrollmentService, Services.EnrollmentService>();
        services.AddSingleton<ISecondChoiceDistributor, SecondChoiceDistributor>();
        return services;
    }

    public static IServiceCollection AddWorkerProcessing(
        this IServiceCollection services,
        bool migrateOnly)
    {
        services.AddSingleton<ISlowOperationSimulator, SlowOperationSimulator>();
        services.AddSingleton<ISafeEnrollmentProcessor, SafeEnrollmentProcessor>();
        services.AddSingleton<IUnsafeEnrollmentProcessor, UnsafeEnrollmentProcessor>();
        services.AddSingleton<IEnrollmentRequestProcessor, EnrollmentRequestProcessor>();
        services.AddSingleton<EnrollmentWorkerPool>();

        if (!migrateOnly)
        {
            services.AddHostedService(sp => sp.GetRequiredService<EnrollmentWorkerPool>());
        }

        return services;
    }

    public static IServiceCollection AddObservability(this IServiceCollection services)
    {
        services.AddSingleton<MetricsCollector>();
        return services;
    }
}
