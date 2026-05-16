using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ProxyWard.Api.Middleware;

internal static class ToolsListResponseFilter
{
    private const string JsonContentType = MediaTypeNames.Application.Json;
    private const string EventStreamContentType = MediaTypeNames.Text.EventStream;

    public static HashSet<string> CreateToolNameSet(IEnumerable<string?> toolNames)
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

    public static bool TryCreateFilteredResponse(
        byte[] body,
        string? contentType,
        string? contentEncoding,
        int maxDecodedBytes,
        IReadOnlySet<string> removedToolNames,
        out FilteredToolsListResponse filteredResponse)
    {
        filteredResponse = default!;
        return removedToolNames.Count != 0
            && TryCreateFilteredResponse(
                body,
                contentType,
                contentEncoding,
                maxDecodedBytes,
                removedToolNames.Contains,
                out filteredResponse);
    }

    public static bool TryCreateFilteredResponse(
        byte[] body,
        string? contentType,
        string? contentEncoding,
        int maxDecodedBytes,
        Func<string, bool> shouldRemoveTool,
        out FilteredToolsListResponse filteredResponse)
    {
        filteredResponse = default!;
        if (!ResponseBodyDecoder.TryDecode(body, contentEncoding, maxDecodedBytes, out var decodedBody))
        {
            return false;
        }

        if (IsEventStream(contentType))
        {
            return TryCreateFilteredEventStreamResponse(
                decodedBody,
                contentType,
                contentEncoding,
                shouldRemoveTool,
                out filteredResponse);
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

        var foundToolsList = FilterToolsListPayload(root, shouldRemoveTool);

        if (!foundToolsList || root is null)
        {
            return false;
        }

        var filteredBody = Encoding.UTF8.GetBytes(root.ToJsonString());
        filteredResponse = CreateFilteredResponse(filteredBody, JsonContentType, contentEncoding);
        return true;
    }

    private static FilteredToolsListResponse CreateFilteredResponse(
        byte[] decodedBody,
        string contentType,
        string? contentEncoding)
    {
        if (TryCreateGzipFilteredResponse(decodedBody, contentType, contentEncoding, out var gzipResponse))
        {
            return gzipResponse;
        }

        return new FilteredToolsListResponse(decodedBody, contentType, ContentEncoding: null);
    }

    private static bool TryCreateGzipFilteredResponse(
        byte[] decodedBody,
        string contentType,
        string? contentEncoding,
        out FilteredToolsListResponse filteredResponse)
    {
        filteredResponse = default!;
        if (string.IsNullOrWhiteSpace(contentEncoding))
        {
            return false;
        }

        var encodings = contentEncoding
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (encodings.Length != 1)
        {
            return false;
        }

        var encoding = encodings[0];
        if (!string.Equals(encoding, "gzip", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(encoding, "x-gzip", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(decodedBody);
        }

        filteredResponse = new FilteredToolsListResponse(compressed.ToArray(), contentType, encoding);
        return true;
    }

    private static bool TryCreateFilteredEventStreamResponse(
        byte[] decodedBody,
        string? contentType,
        string? contentEncoding,
        Func<string, bool> shouldRemoveTool,
        out FilteredToolsListResponse filteredResponse)
    {
        filteredResponse = default!;
        var eventStream = Encoding.UTF8.GetString(decodedBody);
        if (!ToolsListEventStreamFilter.TryFilter(eventStream, shouldRemoveTool, out var filteredEventStream))
        {
            return false;
        }

        var filteredBody = Encoding.UTF8.GetBytes(filteredEventStream);
        filteredResponse = CreateFilteredResponse(
            filteredBody,
            string.IsNullOrWhiteSpace(contentType) ? EventStreamContentType : contentType,
            contentEncoding);
        return true;
    }

    internal static bool FilterToolsListPayload(JsonNode? root, Func<string, bool> shouldRemoveTool) =>
        root switch
        {
            JsonObject response => FilterResponse(response, shouldRemoveTool),
            JsonArray batch => FilterBatch(batch, shouldRemoveTool),
            _ => false
        };

    private static bool FilterBatch(JsonArray batch, Func<string, bool> shouldRemoveTool)
    {
        var foundToolsList = false;
        foreach (var item in batch)
        {
            if (item is JsonObject response)
            {
                foundToolsList |= FilterResponse(response, shouldRemoveTool);
            }
        }

        return foundToolsList;
    }

    private static bool FilterResponse(JsonObject response, Func<string, bool> shouldRemoveTool)
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
                && shouldRemoveTool(toolName))
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

        return string.Equals(mediaType.Trim(), MediaTypeNames.Text.EventStream, StringComparison.OrdinalIgnoreCase);
    }
}
