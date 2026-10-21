using System.Globalization;
using Monday.Application.Abstractions;
using Monday.Application.Projects;
using Monday.Domain.Access;
using Monday.Domain.Agent;
using Monday.Domain.Audit;
using Monday.Domain.Common;
using Monday.Domain.Projects;

namespace Monday.Application.Agent;

/// <summary>What an Owner or Admin sends to change the model. A null <see cref="ApiKey"/> keeps the stored key.</summary>
public sealed record ModelSettingsInput(ModelProvider Provider, string Model, string? BaseUrl, string? ApiKey, long? MonthlyTokenCap)
{
    /// <summary>Keeps the plain key out of ToString, so logging an input never writes the key.</summary>
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append(CultureInfo.InvariantCulture,
            $"Provider = {Provider}, Model = {Model}, BaseUrl = {BaseUrl}, ApiKey = {(ApiKey is null ? "(kept)" : "(redacted)")}, MonthlyTokenCap = {MonthlyTokenCap}");
        return true;
    }
}

/// <summary>Model settings as the settings screen sees them. Never the key: only its last 4 characters.</summary>
public sealed record ModelSettingsView(
    bool Enabled,
    bool Configured,
    ModelProvider? Provider,
    string? Model,
    string? BaseUrl,
    string? KeyLast4,
    long? MonthlyTokenCap,
    string? UpdatedBy,
    DateTimeOffset? UpdatedAt);

/// <summary>Tokens used this month (UTC) against the cap.</summary>
public sealed record ModelUsageView(string Month, long InputTokens, long OutputTokens, long? MonthlyTokenCap, bool CapReached);

/// <summary>Use cases for a project's model settings and usage. Only Owners and Admins see or change them.</summary>
public sealed class ModelSettingsService(
    IProjectRepository projects, IAuditLog audit, IClock clock, ISecretBox secrets, IModelEndpointGuard endpoints, IModelUsageStore usage)
{
    public async Task SaveAsync(ProjectId id, ModelSettingsInput input, UserId by, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        var project = await LoadForManagerAsync(id, by, ct).ConfigureAwait(false);
        Guard.That(secrets.Enabled, "model.keys_disabled", "Model settings are disabled: MONDAY_SECRETS_KEY is not set.");

        Uri? baseUrl = null;
        if (!string.IsNullOrWhiteSpace(input.BaseUrl))
        {
            Guard.That(Uri.TryCreate(input.BaseUrl.Trim(), UriKind.Absolute, out baseUrl), "model.base_url_invalid", "The base URL must be a full URL.");
        }

        string cipher, last4;
        if (!string.IsNullOrWhiteSpace(input.ApiKey))
        {
            var key = input.ApiKey.Trim();
            last4 = ModelSettings.LastFourOf(key);
            cipher = secrets.Seal(key);
        }
        else
        {
            var stored = project.Model ?? throw new DomainException("model.key_required", "A model API key is required.");
            (cipher, last4) = (stored.KeyCipher, stored.KeyLast4);
        }

        var settings = ModelSettings.Create(input.Provider, input.Model, baseUrl, cipher, last4, input.MonthlyTokenCap);
        if (settings.BaseUrl is not null)
        {
            await endpoints.EnsurePublicAsync(settings.BaseUrl, ct).ConfigureAwait(false);
        }

        project.SetModel(settings, by, clock.Now);
        await SaveAsync(project, ct).ConfigureAwait(false);
    }

    public async Task ClearAsync(ProjectId id, UserId by, CancellationToken ct)
    {
        var project = await LoadForManagerAsync(id, by, ct).ConfigureAwait(false);
        project.ClearModel(by, clock.Now);
        await SaveAsync(project, ct).ConfigureAwait(false);
    }

    public async Task<ModelSettingsView> GetAsync(ProjectId id, UserId by, CancellationToken ct)
    {
        var project = await LoadForManagerAsync(id, by, ct).ConfigureAwait(false);
        var m = project.Model;
        return m is null || !secrets.Enabled
            ? new ModelSettingsView(secrets.Enabled, false, null, null, null, null, null, null, null)
            : new ModelSettingsView(true, true, m.Provider, m.Model, m.BaseUrl?.ToString(), m.KeyLast4, m.MonthlyTokenCap,
                project.ModelUpdatedBy?.Value, project.ModelUpdatedAt);
    }

    public async Task<ModelUsageView> UsageAsync(ProjectId id, UserId by, CancellationToken ct)
    {
        var project = await LoadForManagerAsync(id, by, ct).ConfigureAwait(false);
        var month = MonthOf(clock.Now);
        var used = await usage.MonthAsync(id, month, ct).ConfigureAwait(false);
        var cap = project.Model?.MonthlyTokenCap;
        long total = (long)used.InputTokens + used.OutputTokens;
        return new ModelUsageView(month, used.InputTokens, used.OutputTokens, cap, cap is { } c && total >= c);
    }

    /// <summary>The usage month for a moment: <c>yyyy-MM</c> in UTC.</summary>
    public static string MonthOf(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    /// <summary>Non-members get "not found"; members without ManageMembersAndKeys are refused.</summary>
    private async Task<Project> LoadForManagerAsync(ProjectId id, UserId user, CancellationToken ct)
    {
        var project = await projects.GetAsync(id, ct).ConfigureAwait(false);
        if (project is null || project.RoleOf(user) is null)
        {
            throw new NotFoundException("project");
        }

        project.EnsureCan(user, Permission.ManageMembersAndKeys);
        return project;
    }

    /// <summary>Saves, then writes every audited event to the audit log (the same way ProjectService does).</summary>
    private async Task SaveAsync(Project project, CancellationToken ct)
    {
        await projects.SaveAsync(project, ct).ConfigureAwait(false);
        foreach (var domainEvent in project.DomainEvents.Where(AuditEntry.IsAudited))
        {
            await audit.AppendAsync(AuditEntry.For(domainEvent), ct).ConfigureAwait(false);
        }

        project.ClearDomainEvents();
    }
}
