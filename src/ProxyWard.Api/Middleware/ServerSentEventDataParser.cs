using System.Text;

namespace ProxyWard.Api.Middleware;

internal static class ServerSentEventDataParser
{
    public static IReadOnlyList<string> ExtractDataPayloads(byte[] body)
    {
        var text = Encoding.UTF8.GetString(body);
        var payloads = new List<string>();
        var currentDataLines = new List<string>();

        foreach (var rawLine in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = rawLine.EndsWith('\r') ? rawLine[..^1] : rawLine;
            if (line.Length == 0)
            {
                FlushEventData(currentDataLines, payloads);
                continue;
            }

            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var data = line[5..];
            currentDataLines.Add(data.StartsWith(' ') ? data[1..] : data);
        }

        FlushEventData(currentDataLines, payloads);
        return payloads;
    }

    private static void FlushEventData(List<string> currentDataLines, List<string> payloads)
    {
        if (currentDataLines.Count == 0)
        {
            return;
        }

        payloads.Add(string.Join('\n', currentDataLines));
        currentDataLines.Clear();
    }
}
