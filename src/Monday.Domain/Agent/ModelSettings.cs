using Monday.Domain.Common;
using Monday.Domain.Projects;

namespace Monday.Domain.Agent;

/// <summary>Which model API a project talks to.</summary>
public enum ModelProvider
{
    Anthropic,
    OpenAiCompatible,
    Gemini,
}

/// <summary>
/// The model a project's chat uses, and its key, sealed. Monday never holds the plain key here: only
/// the cipher (opened by the secret box at call time) and its last 4 characters, for display.
/// </summary>
public sealed record ModelSettings
{
    private const int MinKeyLength = 8;

    private ModelSettings(ModelProvider provider, string model, Uri? baseUrl, string keyCipher, string keyLast4, long? monthlyTokenCap)
    {
        Provider = provider;
        Model = model;
        BaseUrl = baseUrl;
        KeyCipher = keyCipher;
        KeyLast4 = keyLast4;
        MonthlyTokenCap = monthlyTokenCap;
    }

    public ModelProvider Provider { get; }

    public string Model { get; }

    /// <summary>Only for OpenAI-compatible providers; HTTPS, and never an address inside Monday's network.</summary>
    public Uri? BaseUrl { get; }

    /// <summary>The sealed key (<c>v1:nonce:cipher:tag</c>). Never returned by the API.</summary>
    public string KeyCipher { get; }

    public string KeyLast4 { get; }

    /// <summary>Input plus output tokens a month; at the cap, chat pauses. Null means no cap.</summary>
    public long? MonthlyTokenCap { get; }

    public static ModelSettings Create(ModelProvider provider, string model, Uri? baseUrl, string keyCipher, string keyLast4, long? monthlyTokenCap)
    {
        Guard.That(Enum.IsDefined(provider), "model.provider_unknown", "Unknown model provider.");
        var name = Guard.NotBlank(model, "model.name_required", "A model name is required", 200);
        if (baseUrl is not null)
        {
            Guard.That(provider == ModelProvider.OpenAiCompatible, "model.base_url_only_openai", "A base URL can only be set for an OpenAI-compatible provider.");
            Guard.That(baseUrl.IsAbsoluteUri, "model.base_url_invalid", "The base URL must be a full URL.");
            Guard.That(baseUrl.Scheme == Uri.UriSchemeHttps, "model.base_url_https", "The base URL must use HTTPS.");
            Guard.That(string.IsNullOrEmpty(baseUrl.UserInfo) && string.IsNullOrEmpty(baseUrl.Query) && string.IsNullOrEmpty(baseUrl.Fragment),
                "model.base_url_invalid", "The base URL can't carry credentials, a query or a fragment.");
        }

        var cipher = Guard.NotBlank(keyCipher, "model.key_required", "A model API key is required");
        var last4 = Guard.NotBlank(keyLast4, "model.key_required", "A model API key is required", 4);
        Guard.That(monthlyTokenCap is null or > 0, "model.cap_positive", "The monthly token cap must be more than zero.");
        return new ModelSettings(provider, name, baseUrl, cipher, last4, monthlyTokenCap);
    }

    /// <summary>The last 4 characters shown for a key. Short keys are refused, so the hint never gives most of a key away.</summary>
    public static string LastFourOf(string plainKey)
    {
        var key = Guard.NotBlank(plainKey, "model.key_required", "A model API key is required", 1024);
        Guard.That(key.Length >= MinKeyLength, "model.key_too_short", $"A model API key has at least {MinKeyLength} characters.");
        return key[^4..];
    }

    /// <summary>Keeps the cipher out of ToString, so a logged settings object shows only the last 4.</summary>
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append(System.Globalization.CultureInfo.InvariantCulture,
            $"Provider = {Provider}, Model = {Model}, BaseUrl = {BaseUrl}, KeyLast4 = {KeyLast4}, MonthlyTokenCap = {MonthlyTokenCap}");
        return true;
    }

    /// <summary>
    /// True when <paramref name="next"/> reuses this sealed key for a different provider or base URL.
    /// A stored key only goes where it was set to go; anywhere else needs the key typed again.
    /// </summary>
    internal bool MovesKeyElsewhere(ModelSettings next) =>
        string.Equals(next.KeyCipher, KeyCipher, StringComparison.Ordinal)
        && (next.Provider != Provider || next.BaseUrl != BaseUrl);
}

/// <summary>Carries the base URL's host only (never its path) and the cap before and after; never the key.</summary>
public sealed record ModelSettingsChanged(
    ProjectId ProjectId, ModelProvider Provider, string Model, string? BaseUrlHost, long? OldCap, long? NewCap, bool KeyReplaced, UserId By, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record ModelSettingsCleared(ProjectId ProjectId, UserId By, DateTimeOffset OccurredAt) : IDomainEvent;
