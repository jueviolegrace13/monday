using Monday.Domain.Access;
using Monday.Domain.Agent;
using Monday.Domain.Audit;
using Monday.Domain.Common;
using Monday.Domain.Projects;

namespace Monday.Domain.Tests;

/// <summary>A project's model settings: who may change them, and which base URLs and caps are allowed.</summary>
internal static class ModelSettingsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 7, 0, 0, TimeSpan.Zero);
    private static readonly UserId Owner = new("github:rendee");
    private static readonly UserId Ana = new("github:ana");
    private static readonly UserId Ben = new("github:ben");

    private static ModelSettings Anthropic(string cipher = "v1:a:b:c") =>
        ModelSettings.Create(ModelProvider.Anthropic, "claude-sonnet-4-5", null, cipher, "wxyz", null);

    public static void OnlyOwnersAndAdminsSetModel()
    {
        var project = Project.Create("Monday", Owner, Now);
        project.SetMember(Ana, Role.Debugger, Owner, Now);
        project.SetMember(Ben, Role.Admin, Owner, Now);

        var ex = Assert.Throws<DomainException>(() => project.SetModel(Anthropic(), Ana, Now));
        Assert.True(ex.Code.StartsWith("access.", StringComparison.Ordinal), $"Debugger refusal should be an access code, got {ex.Code}.");
        Assert.True(project.Model is null, "A refused change leaves no settings.");
        Assert.Throws<DomainException>(() => project.ClearModel(Ana, Now));

        project.SetModel(Anthropic(), Ben, Now);
        Assert.Equal(ModelProvider.Anthropic, project.Model!.Provider);
        Assert.Equal(Ben, project.ModelUpdatedBy);

        project.SetModel(Anthropic(), Owner, Now.AddMinutes(1));
        Assert.Equal(Owner, project.ModelUpdatedBy);
        Assert.Equal(Now.AddMinutes(1), project.ModelUpdatedAt);
    }

    public static void HttpBaseUrlRefused()
    {
        Assert.Throws<DomainException>(
            () => ModelSettings.Create(ModelProvider.OpenAiCompatible, "gpt-4o", new Uri("http://api.example.com/v1"), "v1:a:b:c", "wxyz", null),
            "model.base_url_https");
        Assert.Throws<DomainException>(
            () => ModelSettings.Create(ModelProvider.OpenAiCompatible, "gpt-4o", new Uri("ftp://api.example.com/v1"), "v1:a:b:c", "wxyz", null),
            "model.base_url_https");
        Assert.Throws<DomainException>(
            () => ModelSettings.Create(ModelProvider.OpenAiCompatible, "gpt-4o", new Uri("https://user:pass@api.example.com/v1"), "v1:a:b:c", "wxyz", null),
            "model.base_url_invalid");

        var ok = ModelSettings.Create(ModelProvider.OpenAiCompatible, "gpt-4o", new Uri("https://api.example.com/v1"), "v1:a:b:c", "wxyz", null);
        Assert.Equal("https://api.example.com/v1", ok.BaseUrl!.ToString());
    }

    public static void BaseUrlOnlyForOpenAiCompatible()
    {
        var url = new Uri("https://api.example.com/v1");
        Assert.Throws<DomainException>(() => ModelSettings.Create(ModelProvider.Anthropic, "claude", url, "v1:a:b:c", "wxyz", null), "model.base_url_only_openai");
        Assert.Throws<DomainException>(() => ModelSettings.Create(ModelProvider.Gemini, "gemini", url, "v1:a:b:c", "wxyz", null), "model.base_url_only_openai");
    }

    public static void MonthlyCapMustBePositive()
    {
        Assert.Throws<DomainException>(() => ModelSettings.Create(ModelProvider.Anthropic, "claude", null, "v1:a:b:c", "wxyz", 0), "model.cap_positive");
        Assert.Throws<DomainException>(() => ModelSettings.Create(ModelProvider.Anthropic, "claude", null, "v1:a:b:c", "wxyz", -5), "model.cap_positive");
        Assert.Equal(1_000_000L, ModelSettings.Create(ModelProvider.Anthropic, "claude", null, "v1:a:b:c", "wxyz", 1_000_000).MonthlyTokenCap);
    }

    public static void ModelNameAndKeyRequired()
    {
        Assert.Throws<DomainException>(() => ModelSettings.Create(ModelProvider.Anthropic, " ", null, "v1:a:b:c", "wxyz", null), "model.name_required");
        Assert.Throws<DomainException>(() => ModelSettings.Create(ModelProvider.Anthropic, "claude", null, "", "wxyz", null), "model.key_required");
        Assert.Throws<DomainException>(() => ModelSettings.Create((ModelProvider)42, "claude", null, "v1:a:b:c", "wxyz", null), "model.provider_unknown");
    }

    public static void LastFourNeedsAKeyLongEnoughNotToRevealIt()
    {
        Assert.Equal("wxyz", ModelSettings.LastFourOf("  sk-ant-abcdwxyz "));
        Assert.Throws<DomainException>(() => ModelSettings.LastFourOf("short"), "model.key_too_short");
    }

    public static void StoredKeyStaysOnlyWithTheSameProviderAndBaseUrl()
    {
        var project = Project.Create("Monday", Owner, Now);
        var url = new Uri("https://api.example.com/v1");
        project.SetModel(ModelSettings.Create(ModelProvider.OpenAiCompatible, "gpt-4o", url, "v1:k:e:y", "wxyz", null), Owner, Now);

        // Same key, same host: fine (a new model name or cap).
        project.SetModel(ModelSettings.Create(ModelProvider.OpenAiCompatible, "gpt-4.1", url, "v1:k:e:y", "wxyz", 500), Owner, Now);

        // Same key, new base URL: refused, or an Admin could send the stored key to a server of their choosing.
        Assert.Throws<DomainException>(
            () => project.SetModel(ModelSettings.Create(ModelProvider.OpenAiCompatible, "gpt-4o", new Uri("https://evil.example.net/v1"), "v1:k:e:y", "wxyz", null), Owner, Now),
            "model.key_required");
        Assert.Throws<DomainException>(
            () => project.SetModel(ModelSettings.Create(ModelProvider.Anthropic, "claude", null, "v1:k:e:y", "wxyz", null), Owner, Now),
            "model.key_required");

        // A new key with a new base URL is fine.
        project.SetModel(ModelSettings.Create(ModelProvider.OpenAiCompatible, "gpt-4o", new Uri("https://other.example.net/v1"), "v1:n:e:w", "abcd", null), Owner, Now);
        Assert.Equal("abcd", project.Model!.KeyLast4);
    }

    public static void ModelChangesAreAuditedWithoutTheKey()
    {
        var project = Project.Create("Monday", Owner, Now);
        project.ClearDomainEvents();
        project.SetModel(Anthropic("v1:SECRETCIPHER:b:c"), Owner, Now);
        project.ClearModel(Owner, Now.AddMinutes(1));

        var entries = project.DomainEvents.Where(AuditEntry.IsAudited).Select(AuditEntry.For).ToList();
        Assert.Equal(2, entries.Count);
        Assert.Equal("model.settings_changed", entries[0].Action);
        Assert.Equal("model.settings_cleared", entries[1].Action);
        Assert.True(entries.TrueForAll(e => !e.Detail.Contains("SECRETCIPHER", StringComparison.Ordinal) && !e.Detail.Contains("wxyz", StringComparison.Ordinal)),
            "Audit details never hold the key or its cipher.");
        Assert.True(project.Model is null, "Clear removes the settings.");
    }

    public static void ModelAuditNamesTheHostAndTheCapChange()
    {
        var project = Project.Create("Monday", Owner, Now);
        project.ClearDomainEvents();
        project.SetModel(ModelSettings.Create(ModelProvider.OpenAiCompatible, "gpt-4o", new Uri("https://api.example.com/v1/secret-path"), "v1:SECRET:b:c", "wxyz", 1000), Owner, Now);
        project.SetModel(ModelSettings.Create(ModelProvider.OpenAiCompatible, "gpt-4o", new Uri("https://api.example.com/v1/secret-path"), "v1:SECRET:b:c", "wxyz", 2000), Owner, Now);
        project.SetModel(Anthropic(), Owner, Now);

        var details = project.DomainEvents.Where(AuditEntry.IsAudited).Select(e => AuditEntry.For(e).Detail).ToList();
        Assert.Equal("OpenAiCompatible gpt-4o at api.example.com, cap none -> 1000, new key", details[0]);
        Assert.Equal("OpenAiCompatible gpt-4o at api.example.com, cap 1000 -> 2000", details[1]);
        Assert.Equal("Anthropic claude-sonnet-4-5, cap 2000 -> none, new key", details[2]);
    }
}
