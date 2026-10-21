using System.Text.Json;
using System.Text.Json.Serialization;
using Monday.Domain.Common;

namespace Monday.Application.Agent.Tools;

/// <summary>Shared limits and JSON helpers, so every tool reads its input and caps its output the same way.</summary>
internal static class ToolInput
{
    /// <summary>Most items any list result carries, so one call cannot flood the model's context.</summary>
    public const int MaxItems = 50;

    /// <summary>Most characters of source code any result carries.</summary>
    public const int MaxCodeChars = 4000;

    public static readonly TimeSpan DefaultWindow = TimeSpan.FromHours(1);

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static JsonElement Schema(string json) => JsonDocument.Parse(json).RootElement.Clone();

    public static JsonElement Data(object value) => JsonSerializer.SerializeToElement(value, Options);

    public static string? String(JsonElement input, string name) =>
        input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    public static string RequiredString(JsonElement input, string name) =>
        String(input, name) is { Length: > 0 } s ? s : throw Invalid($"'{name}' is required.");

    public static int Count(JsonElement input, string name, int fallback) =>
        input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var p) && p.TryGetInt32(out var n)
            ? Math.Clamp(n, 1, MaxItems)
            : fallback;

    public static DateTimeOffset? Time(JsonElement input, string name) =>
        String(input, name) is { } s
            ? DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var t) ? t : throw Invalid($"'{name}' must be an ISO time.")
            : null;

    /// <summary>The window asked for, or the last hour up to <paramref name="now"/>.</summary>
    public static (DateTimeOffset Since, DateTimeOffset Until) Window(JsonElement input, DateTimeOffset now)
    {
        var until = Time(input, "until") ?? now;
        return (Time(input, "since") ?? until - DefaultWindow, until);
    }

    public static DomainException Invalid(string message) => new("agent.input", message);
}
