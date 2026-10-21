using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Monday.Application.Abstractions;

namespace Monday.Infrastructure.Models;

/// <summary>What both adapters share: sending the request, mapping failures to <see cref="ModelException"/>, never naming the key.</summary>
internal static class ModelHttp
{
    public static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpRequestMessage request, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw Translate(ex);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ModelException("model.unavailable", "The model provider timed out.");
        }

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        var status = response.StatusCode;
        response.Dispose();
        throw status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new ModelException("model.auth", "The model provider refused the key."),
            HttpStatusCode.TooManyRequests => new ModelException("model.rate_limited", "The model provider is rate limiting this key."),
            >= HttpStatusCode.InternalServerError => new ModelException("model.unavailable", "The model provider is unavailable."),
            _ => new ModelException("model.rejected", "The model provider rejected the request."),
        };
    }

    /// <summary>Yields the inner stream, turning a mid-stream network failure into a ModelException.</summary>
    public static async IAsyncEnumerable<T> GuardAsync<T>(IAsyncEnumerable<T> inner)
    {
        await using var enumerator = inner.GetAsyncEnumerator();
        while (true)
        {
            bool more;
            try
            {
                more = await enumerator.MoveNextAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                throw Translate(ex);
            }

            if (!more)
            {
                yield break;
            }

            yield return enumerator.Current;
        }
    }

    public static StringContent Json(JsonNode body) => new(body.ToJsonString(), Encoding.UTF8, "application/json");

    /// <summary>Raw JSON text of an element, or {} when it was never set.</summary>
    public static JsonNode ArgsNode(JsonElement args) =>
        args.ValueKind == JsonValueKind.Undefined ? new JsonObject() : JsonNode.Parse(args.GetRawText()) ?? new JsonObject();

    public static string RawText(JsonElement element) => element.ValueKind == JsonValueKind.Undefined ? "{}" : element.GetRawText();

    /// <summary>The "_invalid" marker the loop turns into a tool error.</summary>
    public static JsonElement ParseArgs(string raw)
    {
        var text = string.IsNullOrWhiteSpace(raw) ? "{}" : raw;
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                return doc.RootElement.Clone();
            }
        }
        catch (JsonException)
        {
            // fall through to the marker
        }

        return JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["_invalid"] = raw });
    }

    private static Exception Translate(Exception ex)
    {
        for (Exception? inner = ex; inner is not null; inner = inner.InnerException)
        {
            if (inner is UnsafeEndpointException)
            {
                return inner;
            }
        }

        return new ModelException("model.unavailable", "The model provider could not be reached.");
    }
}
