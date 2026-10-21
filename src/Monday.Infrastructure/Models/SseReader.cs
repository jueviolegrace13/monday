using System.Runtime.CompilerServices;
using System.Text;

namespace Monday.Infrastructure.Models;

/// <summary>Reads a server-sent-events stream: one (event, data) pair per blank-line-terminated block.</summary>
public static class SseReader
{
    public static async IAsyncEnumerable<(string Event, string Data)> ReadAsync(Stream stream, [EnumeratorCancellation] CancellationToken ct, Action? onActivity = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var eventName = string.Empty;
        var data = new StringBuilder();
        var hasData = false;

        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            onActivity?.Invoke(); // any line, even a keep-alive comment, shows the stream is alive
            if (line.Length == 0)
            {
                if (hasData)
                {
                    yield return (eventName, data.ToString());
                }

                eventName = string.Empty;
                data.Clear();
                hasData = false;
                continue;
            }

            if (line[0] == ':')
            {
                continue; // comment / keep-alive
            }

            var colon = line.IndexOf(':', StringComparison.Ordinal);
            var field = colon < 0 ? line : line[..colon];
            var value = colon < 0 ? string.Empty : line[(colon + 1)..];
            if (value.StartsWith(' '))
            {
                value = value[1..]; // the spec strips exactly one space
            }

            if (field == "event")
            {
                eventName = value;
            }
            else if (field == "data")
            {
                if (hasData)
                {
                    data.Append('\n');
                }

                data.Append(value);
                hasData = true;
            }
        }

        if (hasData)
        {
            yield return (eventName, data.ToString());
        }
    }
}
