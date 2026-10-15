using Microsoft.Extensions.DependencyInjection;
using Monday.Application.Abstractions;
using Monday.Application.Activity;
using Monday.Application.CodeGraph;
using Monday.Application.Decisions;
using Monday.Application.Projects;
using Monday.Infrastructure.InMemory;

namespace Monday.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Registers Monday's use cases and, for now, in-memory stores.</summary>
    public static IServiceCollection AddMonday(this IServiceCollection services)
    {
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IProjectRepository, InMemoryProjectRepository>();
        services.AddSingleton<IGraphStore, InMemoryGraphStore>();
        services.AddSingleton<IActivityStore, InMemoryActivityStore>();
        services.AddSingleton<IDecisionRepository, InMemoryDecisionRepository>();
        services.AddSingleton<IAuditLog, InMemoryAuditLog>();

        services.AddScoped<ApiKeyAuthenticator>();
        services.AddScoped<ProjectService>();
        services.AddScoped<GraphService>();
        services.AddScoped<ActivityService>();
        services.AddScoped<DecisionService>();
        return services;
    }
}
