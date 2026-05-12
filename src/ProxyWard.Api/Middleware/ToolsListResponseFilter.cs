using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ProxyWard.Api.Middleware;

internal static class ToolsListResponseFilter
{
    public static HashSet<string> CreateBlockedToolNameSet(IEnumerable<string?> toolNames)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var toolName in toolNames)
        {
            if (!string.IsNullOrWhiteSpace(toolName))
            {
                names.Add(toolName);
            }
        }

        return names;
    }

    public static bool TryCreateFilteredBody(
        byte[] body,
        string? contentType,
        string? contentEncoding,
        int maxDecodedBytes,
        IReadOnlySet<string> blockedToolNames,
        out byte[] filteredBody)
    {
        filteredBody = [];
        if (blockedToolNames.Count == 0 || IsEventStream(contentType))
        {
            return false;
        }

        if (!ResponseBodyDecoder.TryDecode(body, contentEncoding, maxDecodedBytes, out var decodedBody))
        {
            return false;
        }

        JsonNode? root;
        try
        {
            var reader = new Utf8JsonReader(decodedBody);
            root = JsonNode.Parse(ref reader);
        }
        catch (JsonException)
        {
            return false;
        }

        var foundToolsList = root switch
        {
            JsonObject response => FilterResponse(response, blockedToolNames),
            JsonArray batch => FilterBatch(batch, blockedToolNames),
            _ => false
        };

        if (!foundToolsList || root is null)
        {
            return false;
        }

        filteredBody = Encoding.UTF8.GetBytes(root.ToJsonString());
        return true;
    }

    private static bool FilterBatch(JsonArray batch, IReadOnlySet<string> blockedToolNames)
    {
        var foundToolsList = false;
        foreach (var item in batch)
        {
            if (item is JsonObject response)
            {
                foundToolsList |= FilterResponse(response, blockedToolNames);
            }
        }

        return foundToolsList;
    }

    private static bool FilterResponse(JsonObject response, IReadOnlySet<string> blockedToolNames)
    {
        if (response["result"] is not JsonObject result
            || result["tools"] is not JsonArray tools)
        {
            return false;
        }

        for (var index = tools.Count - 1; index >= 0; index--)
        {
            if (tools[index] is JsonObject tool
                && TryGetString(tool, "name", out var toolName)
                && blockedToolNames.Contains(toolName))
            {
                tools.RemoveAt(index);
            }
        }

        return true;
    }

    private static bool TryGetString(JsonObject jsonObject, string propertyName, out string value)
    {
        value = string.Empty;
        if (!jsonObject.TryGetPropertyValue(propertyName, out var node)
            || node is not JsonValue jsonValue
            || !jsonValue.TryGetValue<string>(out var text)
            || string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        value = text;
        return true;
    }

    private static bool IsEventStream(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return false;
        }

        var delimiterIndex = contentType.IndexOf(';', StringComparison.Ordinal);
        var mediaType = delimiterIndex >= 0
            ? contentType[..delimiterIndex]
            : contentType;

        return string.Equals(mediaType.Trim(), "text/event-stream", StringComparison.OrdinalIgnoreCase);
    }
}
