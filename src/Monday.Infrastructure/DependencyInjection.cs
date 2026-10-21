using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Monday.Application.Abstractions;
using Monday.Application.Access;
using Monday.Application.Activity;
using Monday.Application.Agent;
using Monday.Application.Agent.Tools;
using Monday.Application.CodeGraph;
using Monday.Application.Decisions;
using Monday.Application.Projects;
using Monday.Application.Traffic;
using Monday.Infrastructure.InMemory;
using Monday.Infrastructure.Models;
using Monday.Infrastructure.Postgres;
using Monday.Infrastructure.Secrets;
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

        // Model keys are sealed under MONDAY_SECRETS_KEY (environment). Missing: the API starts, model settings are off.
        services.AddSingleton<ISecretBox>(new AesGcmSecretBox(configuration));
        services.AddSingleton<IModelEndpointGuard, SafeEndpointGuard>();

        // Every model call dials through SafeEndpoint's handler, which re-checks the address it connects to.
        services.AddHttpClient(ChatModelFactory.ClientName, client => client.Timeout = TimeSpan.FromSeconds(60))
            .ConfigurePrimaryHttpMessageHandler(() => SafeEndpoint.CreateHandler());
        services.AddSingleton<IChatModelFactory, ChatModelFactory>();

        var connection = configuration.GetConnectionString("Monday");
        if (string.IsNullOrWhiteSpace(connection))
        {
            services.AddSingleton<IProjectRepository, InMemoryProjectRepository>();
            services.AddSingleton<IGraphStore, InMemoryGraphStore>();
            services.AddSingleton<IActivityStore, InMemoryActivityStore>();
            services.AddSingleton<IDecisionRepository, InMemoryDecisionRepository>();
            services.AddSingleton<IAuditLog, InMemoryAuditLog>();
            services.AddSingleton<ITrafficStore, InMemoryTrafficStore>();
            services.AddSingleton<IModelUsageStore, InMemoryModelUsageStore>();
            services.AddSingleton<IConversationStore, InMemoryConversationStore>();
            services.AddSingleton<IPersonalTokenStore, InMemoryPersonalTokenStore>();
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
            services.AddSingleton<IModelUsageStore, PostgresModelUsageStore>();
            services.AddSingleton<IConversationStore, PostgresConversationStore>();
            services.AddSingleton<IPersonalTokenStore, PostgresPersonalTokenStore>();
        }

        services.AddScoped<ApiKeyAuthenticator>();
        services.AddScoped<ProjectService>();
        services.AddScoped<GraphService>();
        services.AddScoped<ActivityService>();
        services.AddScoped<DecisionService>();
        services.AddScoped<TrafficService>();
        services.AddScoped<ModelSettingsService>();
        services.AddScoped<PersonalTokenService>();
        return services;
    }

    /// <summary>
    /// Registers the agent's v1 tools, the registry that filters them by role and channel, and the agent loop. Scoped,
    /// because every tool wraps a scoped use case. Lives here because Monday.Application takes no DI package.
    /// </summary>
    public static IServiceCollection AddAgentTools(this IServiceCollection services)
    {
        services.AddScoped<IAgentTool, ListProjectsTool>();
        services.AddScoped<IAgentTool, GraphOverviewTool>();
        services.AddScoped<IAgentTool, FindNodesTool>();
        services.AddScoped<IAgentTool, NodeDetailTool>();
        services.AddScoped<IAgentTool, TrafficStatsTool>();
        services.AddScoped<IAgentTool, RecentRequestsTool>();
        services.AddScoped<IAgentTool, RecentActivityTool>();
        services.AddScoped<IAgentTool, SecurityFindingsTool>();
        services.AddScoped<IAgentTool, ListDecisionsTool>();
        services.AddScoped<IAgentTool, FocusNodeTool>();
        services.AddScoped<IAgentTool, HighlightPathTool>();
        services.AddScoped<IAgentTool, SetLensTool>();
        services.AddScoped<IAgentTool, StartReplayTool>();
        services.AddScoped<IAgentTool, DraftDecisionTool>();
        services.AddScoped<ToolRegistry>();
        services.AddScoped<AgentService>();
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
