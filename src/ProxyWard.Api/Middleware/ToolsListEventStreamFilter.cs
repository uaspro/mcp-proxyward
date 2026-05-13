using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ProxyWard.Api.Middleware;

internal static class ToolsListEventStreamFilter
{
    public static bool TryFilter(
        string eventStream,
        Func<string, bool> shouldRemoveTool,
        out string filteredEventStream)
    {
        var normalized = eventStream
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal);
        var eventBlocks = normalized.Split("\n\n", StringSplitOptions.None);
        var builder = new StringBuilder(normalized.Length);
        var foundToolsList = false;

        for (var index = 0; index < eventBlocks.Length; index++)
        {
            var eventBlock = eventBlocks[index];
            if (eventBlock.Length != 0
                && TryFilterEventBlock(eventBlock, shouldRemoveTool, out var filteredEventBlock))
            {
                builder.Append(filteredEventBlock);
                foundToolsList = true;
            }
            else
            {
                builder.Append(eventBlock);
            }

            if (index < eventBlocks.Length - 1)
            {
                builder.Append("\n\n");
            }
        }

        filteredEventStream = foundToolsList ? builder.ToString() : string.Empty;
        return foundToolsList;
    }

    private static bool TryFilterEventBlock(
        string eventBlock,
        Func<string, bool> shouldRemoveTool,
        out string filteredEventBlock)
    {
        filteredEventBlock = string.Empty;
        var lines = eventBlock.Split('\n');
        var dataLines = new List<string>();
        string? eventType = null;

        foreach (var line in lines)
        {
            if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
            {
                eventType = ReadFieldValue(line[6..]);
                continue;
            }

            if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                dataLines.Add(ReadFieldValue(line[5..]));
            }
        }

        if (!IsJsonRpcSseMessage(eventType) || dataLines.Count == 0)
        {
            return false;
        }

        var data = string.Join('\n', dataLines).Trim();
        if (data.Length == 0
            || string.Equals(data, "[DONE]", StringComparison.OrdinalIgnoreCase)
            || !LooksLikeJson(data))
        {
            return false;
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(data);
        }
        catch (JsonException)
        {
            return false;
        }

        if (!ToolsListResponseFilter.FilterToolsListPayload(root, shouldRemoveTool) || root is null)
        {
            return false;
        }

        filteredEventBlock = ReplaceEventData(lines, root.ToJsonString());
        return true;
    }

    private static string ReplaceEventData(string[] lines, string data)
    {
        var replacedData = false;
        var output = new List<string>(lines.Length);
        foreach (var line in lines)
        {
            if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                if (!replacedData)
                {
                    output.Add($"data: {data}");
                    replacedData = true;
                }

                continue;
            }

            output.Add(line);
        }

        if (!replacedData)
        {
            output.Add($"data: {data}");
        }

        return string.Join('\n', output);
    }

    private static string ReadFieldValue(string value) =>
        value.StartsWith(' ') ? value[1..] : value;

    private static bool IsJsonRpcSseMessage(string? eventType) =>
        string.IsNullOrWhiteSpace(eventType)
        || string.Equals(eventType, "message", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeJson(string value) =>
        value.StartsWith('{') || value.StartsWith('[');
}
