using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Monday.Application.Abstractions;
using Monday.Application.Activity;
using Monday.Application.CodeGraph;
using Monday.Application.Decisions;
using Monday.Application.Projects;
using Monday.Application.Traffic;
using Monday.Infrastructure.InMemory;
using Monday.Infrastructure.Postgres;
using Npgsql;

namespace Monday.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Registers Monday's use cases and its stores: Postgres when ConnectionStrings:Monday is set
    /// (Neon in production), otherwise in memory for local runs and tests.
    /// </summary>
    public static IServiceCollection AddMonday(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<ITrafficFeed, TrafficFeed>();

        var connection = configuration.GetConnectionString("Monday");
        if (string.IsNullOrWhiteSpace(connection))
        {
            services.AddSingleton<IProjectRepository, InMemoryProjectRepository>();
            services.AddSingleton<IGraphStore, InMemoryGraphStore>();
            services.AddSingleton<IActivityStore, InMemoryActivityStore>();
            services.AddSingleton<IDecisionRepository, InMemoryDecisionRepository>();
            services.AddSingleton<IAuditLog, InMemoryAuditLog>();
            services.AddSingleton<ITrafficStore, InMemoryTrafficStore>();
        }
        else
        {
            services.AddSingleton(_ => NpgsqlDataSource.Create(connection));
            services.AddSingleton<IProjectRepository, PostgresProjectRepository>();
            services.AddSingleton<IGraphStore, PostgresGraphStore>();
            services.AddSingleton<IActivityStore, PostgresActivityStore>();
            services.AddSingleton<IDecisionRepository, PostgresDecisionRepository>();
            services.AddSingleton<IAuditLog, PostgresAuditLog>();
            services.AddSingleton<ITrafficStore, PostgresTrafficStore>();
        }

        services.AddScoped<ApiKeyAuthenticator>();
        services.AddScoped<ProjectService>();
        services.AddScoped<GraphService>();
        services.AddScoped<ActivityService>();
        services.AddScoped<DecisionService>();
        services.AddScoped<TrafficService>();
        return services;
    }

    /// <summary>Brings the database schema up to date before the app takes requests. No-op in memory.</summary>
    public static async Task MigrateMondayAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        if (services.GetService<NpgsqlDataSource>() is { } db)
        {
            await Schema.MigrateAsync(db, ct).ConfigureAwait(false);
        }
    }
}
