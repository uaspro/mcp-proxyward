using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ProxyWard.Core.Policies;

namespace ProxyWard.Locking.Tools;

public sealed class ToolDefinitionExtractor : IToolDefinitionExtractor
{
    private const string EmptyResponseReason = "empty_response";
    private const string EventStreamWithoutDataReason = "event_stream_without_data";
    private const string EventStreamWithoutJsonRpcMessageReason = "event_stream_without_jsonrpc_message";

    public ToolListExtractionResult Extract(ReadOnlyMemory<byte> responseBody) =>
        ExtractJson(responseBody);

    public ToolListExtractionResult Extract(ReadOnlyMemory<byte> responseBody, string? contentType)
    {
        if (IsEmpty(responseBody.Span))
        {
            return ToolListExtractionResult.SkippedInspection(EmptyResponseReason);
        }

        return IsEventStream(contentType)
            ? ExtractEventStream(responseBody)
            : ExtractJson(responseBody);
    }

    private static ToolListExtractionResult ExtractJson(ReadOnlyMemory<byte> responseBody)
    {
        JsonNode? root;

        try
        {
            var reader = new Utf8JsonReader(responseBody.Span);
            root = JsonNode.Parse(ref reader);
        }
        catch (JsonException)
        {
            return ToolListExtractionResult.Failed(PolicyReasonCodes.JsonMalformed);
        }

        var tools = new List<DiscoveredTool>();

        switch (root)
        {
            case JsonObject response:
                ExtractFromResponse(response, tools);
                break;
            case JsonArray batch:
                foreach (var item in batch)
                {
                    if (item is JsonObject batchResponse)
                    {
                        ExtractFromResponse(batchResponse, tools);
                    }
                }

                break;
            default:
                return ToolListExtractionResult.Failed(PolicyReasonCodes.JsonMalformed);
        }

        return ToolListExtractionResult.Extracted(tools);
    }

    private static ToolListExtractionResult ExtractEventStream(ReadOnlyMemory<byte> responseBody)
    {
        var events = ServerSentEventDataParser.ExtractEvents(responseBody);
        if (events.Count == 0)
        {
            return ToolListExtractionResult.SkippedInspection(EventStreamWithoutDataReason);
        }

        var jsonMessages = new JsonArray();
        foreach (var serverSentEvent in events)
        {
            if (!IsJsonRpcSseMessage(serverSentEvent))
            {
                continue;
            }

            var trimmed = serverSentEvent.Data.Trim();
            if (trimmed.Length == 0 || string.Equals(trimmed, "[DONE]", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!LooksLikeJson(trimmed))
            {
                continue;
            }

            JsonNode? message;
            try
            {
                message = JsonNode.Parse(trimmed);
            }
            catch (JsonException)
            {
                return ToolListExtractionResult.Failed(PolicyReasonCodes.JsonMalformed);
            }

            if (message is not null)
            {
                jsonMessages.Add(message);
            }
        }

        if (jsonMessages.Count == 0)
        {
            return ToolListExtractionResult.SkippedInspection(EventStreamWithoutJsonRpcMessageReason);
        }

        var extractableBody = Encoding.UTF8.GetBytes(
            jsonMessages.Count == 1
                ? jsonMessages[0]!.ToJsonString()
                : jsonMessages.ToJsonString());

        return ExtractJson(extractableBody);
    }

    private static void ExtractFromResponse(JsonObject response, ICollection<DiscoveredTool> tools)
    {
        if (response["result"] is not JsonObject result
            || result["tools"] is not JsonArray toolArray)
        {
            return;
        }

        foreach (var item in toolArray)
        {
            if (item is not JsonObject tool)
            {
                continue;
            }

            tools.Add(new DiscoveredTool(
                GetString(tool, "name"),
                GetString(tool, "title"),
                GetString(tool, "description"),
                Clone(tool["inputSchema"]),
                Clone(tool["outputSchema"])));
        }
    }

    private static string? GetString(JsonObject jsonObject, string propertyName) =>
        jsonObject.TryGetPropertyValue(propertyName, out var value)
            && value is JsonValue jsonValue
            && jsonValue.TryGetValue<string>(out var text)
                ? text
                : null;

    private static JsonNode? Clone(JsonNode? node) =>
        node?.DeepClone();

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

    private static bool IsJsonRpcSseMessage(ServerSentEvent serverSentEvent) =>
        string.IsNullOrWhiteSpace(serverSentEvent.EventType)
        || string.Equals(serverSentEvent.EventType, "message", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeJson(string value) =>
        value.StartsWith('{') || value.StartsWith('[');

    private static bool IsEmpty(ReadOnlySpan<byte> body)
    {
        foreach (var value in body)
        {
            if (!char.IsWhiteSpace((char)value))
            {
                return false;
            }
        }

        return true;
    }
}
