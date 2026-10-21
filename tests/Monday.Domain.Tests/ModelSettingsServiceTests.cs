using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Monday.Application.Abstractions;
using Monday.Application.Agent;
using Monday.Application.Projects;
using Monday.Domain.Access;
using Monday.Domain.Agent;
using Monday.Domain.Common;
using Monday.Domain.Projects;
using Monday.Infrastructure.InMemory;
using Monday.Infrastructure.Secrets;

namespace Monday.Domain.Tests;

/// <summary>Saving and reading model settings: the key is sealed, never returned, and the base URL is checked.</summary>
internal static class ModelSettingsServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 7, 0, 0, TimeSpan.Zero);
    private const string PlainKey = "sk-ant-api03-PLAINSECRET-9876";

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset Now => ModelSettingsServiceTests.Now;
    }

    private sealed class RecordingGuard : IModelEndpointGuard
    {
        public List<Uri> Checked { get; } = [];

        public bool Refuse { get; init; }

        public Task EnsurePublicAsync(Uri baseUrl, CancellationToken ct)
        {
            Checked.Add(baseUrl);
            return Refuse ? throw new DomainException("model.base_url_private", "inside") : Task.CompletedTask;
        }
    }

    private sealed class World
    {
        public UserId Owner { get; } = new("github:rendee");
        public UserId Ana { get; } = new("github:ana");
        public ProjectId Project { get; private set; }
        public InMemoryProjectRepository Projects { get; } = new();
        public InMemoryAuditLog Audit { get; } = new();
        public InMemoryModelUsageStore Usage { get; } = new();
        public RecordingGuard Guard { get; private init; } = new();
        public AesGcmSecretBox Box { get; private init; } = null!;
        public ModelSettingsService Service { get; private set; } = null!;

        public static World Build(bool secrets = true, bool refuse = false)
        {
            var key = secrets ? Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) : null;
            var box = new AesGcmSecretBox(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["MONDAY_SECRETS_KEY"] = key }).Build());
            var w = new World { Box = box, Guard = new RecordingGuard { Refuse = refuse } };
            var clock = new FixedClock();
            var projects = new ProjectService(w.Projects, w.Audit, clock);
            w.Project = new ProjectId(projects.CreateAsync("Shop", w.Owner, default).GetAwaiter().GetResult().Id);
            projects.SetMemberAsync(w.Project, w.Ana, Role.Debugger, w.Owner, default).GetAwaiter().GetResult();
            w.Service = new ModelSettingsService(w.Projects, w.Audit, clock, box, w.Guard, w.Usage);
            return w;
        }

        public void Save(ModelSettingsInput input, UserId? by = null) =>
            Service.SaveAsync(Project, input, by ?? Owner, default).GetAwaiter().GetResult();

        public Project Load() => Projects.GetAsync(Project, default).GetAwaiter().GetResult()!;
    }

    public static void SavedKeyIsSealedAndOnlyLastFourIsShown()
    {
        var w = World.Build();
        w.Save(new ModelSettingsInput(ModelProvider.Anthropic, "claude-sonnet-4-5", null, PlainKey, 2_000_000));

        var stored = w.Load().Model!;
        Assert.True(!stored.KeyCipher.Contains("PLAINSECRET", StringComparison.Ordinal), "The stored key is sealed.");
        Assert.Equal(PlainKey, w.Box.Open(stored.KeyCipher));

        var view = w.Service.GetAsync(w.Project, w.Owner, default).GetAwaiter().GetResult();
        Assert.True(view.Enabled && view.Configured, "Settings show as configured.");
        Assert.Equal("9876", view.KeyLast4);
        Assert.Equal(2_000_000L, view.MonthlyTokenCap);
        var shown = System.Text.Json.JsonSerializer.Serialize(view);
        Assert.True(!shown.Contains("PLAINSECRET", StringComparison.Ordinal) && !shown.Contains(stored.KeyCipher, StringComparison.Ordinal), "The view never holds the key or its cipher.");

        var audit = w.Audit.ListAsync(w.Project, default).GetAwaiter().GetResult();
        Assert.True(audit.Any(a => a.Action == "model.settings_changed"), "The change is audited.");
        Assert.True(audit.All(a => !a.Detail.Contains("PLAINSECRET", StringComparison.Ordinal)), "The audit never holds the key.");
    }

    public static void ToStringNeverShowsTheKeyOrCipher()
    {
        var input = new ModelSettingsInput(ModelProvider.Anthropic, "claude", null, PlainKey, null);
        Assert.True(!input.ToString().Contains("PLAINSECRET", StringComparison.Ordinal), "The input's ToString hides the key.");

        var settings = ModelSettings.Create(ModelProvider.Anthropic, "claude", null, "v1:SECRETCIPHER:b:c", "9876", null);
        Assert.True(!settings.ToString().Contains("SECRETCIPHER", StringComparison.Ordinal) && settings.ToString().Contains("9876", StringComparison.Ordinal),
            "The settings' ToString shows the last 4, never the cipher.");
    }

    public static void NullKeyKeepsTheStoredOne()
    {
        var w = World.Build();
        w.Save(new ModelSettingsInput(ModelProvider.Anthropic, "claude-sonnet-4-5", null, PlainKey, null));
        var first = w.Load().Model!.KeyCipher;

        w.Save(new ModelSettingsInput(ModelProvider.Anthropic, "claude-opus-4-1", null, null, 10));

        Assert.Equal(first, w.Load().Model!.KeyCipher);
        Assert.Equal("claude-opus-4-1", w.Load().Model!.Model);
        Assert.Throws<DomainException>(
            () => w.Save(new ModelSettingsInput(ModelProvider.OpenAiCompatible, "gpt-4o", "https://api.example.com/v1", null, null)),
            "model.key_required");
    }

    public static void FirstSaveNeedsAKey()
    {
        var w = World.Build();
        Assert.Throws<DomainException>(() => w.Save(new ModelSettingsInput(ModelProvider.Anthropic, "claude", null, null, null)), "model.key_required");
        Assert.Throws<DomainException>(() => w.Save(new ModelSettingsInput(ModelProvider.Anthropic, "claude", null, "abc", null)), "model.key_too_short");
    }

    public static void DebuggerCannotSaveOrReadSettings()
    {
        var w = World.Build();
        Assert.Throws<DomainException>(() => w.Save(new ModelSettingsInput(ModelProvider.Anthropic, "claude", null, PlainKey, null), w.Ana), "access.denied");
        Assert.Throws<DomainException>(() => w.Service.GetAsync(w.Project, w.Ana, default).GetAwaiter().GetResult(), "access.denied");
        Assert.Throws<NotFoundException>(() => w.Service.GetAsync(w.Project, new UserId("github:stranger"), default).GetAwaiter().GetResult());
    }

    public static void BaseUrlIsCheckedForInsideAddressesOnSave()
    {
        var w = World.Build(refuse: true);
        Assert.Throws<DomainException>(
            () => w.Save(new ModelSettingsInput(ModelProvider.OpenAiCompatible, "gpt-4o", "https://models.internal/v1", PlainKey, null)),
            "model.base_url_private");
        Assert.Equal("https://models.internal/v1", w.Guard.Checked.Single().ToString());
        Assert.True(w.Load().Model is null, "Nothing is saved.");
        Assert.Throws<DomainException>(
            () => w.Save(new ModelSettingsInput(ModelProvider.OpenAiCompatible, "gpt-4o", "not a url", PlainKey, null)),
            "model.base_url_invalid");
    }

    public static void WithoutMasterKeySettingsAreDisabled()
    {
        var w = World.Build(secrets: false);
        Assert.Throws<DomainException>(() => w.Save(new ModelSettingsInput(ModelProvider.Anthropic, "claude", null, PlainKey, null)), "model.keys_disabled");
        var view = w.Service.GetAsync(w.Project, w.Owner, default).GetAwaiter().GetResult();
        Assert.True(!view.Enabled && !view.Configured, "The view says model settings are off.");
    }

    public static void UsageIsCountedPerMonthAgainstTheCap()
    {
        var w = World.Build();
        w.Save(new ModelSettingsInput(ModelProvider.Anthropic, "claude", null, PlainKey, 1_000));
        w.Usage.AddAsync(w.Project, "2026-10", new Usage(400, 300), default).GetAwaiter().GetResult();
        w.Usage.AddAsync(w.Project, "2026-10", new Usage(200, 150), default).GetAwaiter().GetResult();
        w.Usage.AddAsync(w.Project, "2026-09", new Usage(9_000, 9_000), default).GetAwaiter().GetResult();

        var usage = w.Service.UsageAsync(w.Project, w.Owner, default).GetAwaiter().GetResult();

        Assert.Equal("2026-10", usage.Month);
        Assert.Equal(600L, usage.InputTokens);
        Assert.Equal(450L, usage.OutputTokens);
        Assert.Equal(1_000L, usage.MonthlyTokenCap);
        Assert.True(usage.CapReached, "1050 tokens is over a 1000 cap.");
    }
}
