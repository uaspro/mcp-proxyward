using System.Text;

namespace ProxyWard.Locking.Tools;

internal static class ServerSentEventDataParser
{
    public static IReadOnlyList<ServerSentEvent> ExtractEvents(ReadOnlyMemory<byte> body)
    {
        var text = Encoding.UTF8.GetString(body.Span);
        var events = new List<ServerSentEvent>();
        var currentDataLines = new List<string>();
        string? currentEventType = null;

        foreach (var rawLine in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = rawLine.EndsWith('\r') ? rawLine[..^1] : rawLine;
            if (line.Length == 0)
            {
                FlushEventData(currentEventType, currentDataLines, events);
                currentEventType = null;
                continue;
            }

            if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
            {
                currentEventType = ReadFieldValue(line[6..]);
                continue;
            }

            if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                currentDataLines.Add(ReadFieldValue(line[5..]));
            }
        }

        FlushEventData(currentEventType, currentDataLines, events);
        return events;
    }

    private static string ReadFieldValue(string value) =>
        value.StartsWith(' ') ? value[1..] : value;

    private static void FlushEventData(
        string? currentEventType,
        List<string> currentDataLines,
        ICollection<ServerSentEvent> events)
    {
        if (currentDataLines.Count == 0)
        {
            return;
        }

        events.Add(new ServerSentEvent(currentEventType, string.Join('\n', currentDataLines)));
        currentDataLines.Clear();
    }
}

internal sealed record ServerSentEvent(string? EventType, string Data);
