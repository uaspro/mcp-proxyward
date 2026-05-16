using System.Text.Json;
using System.Text.Json.Nodes;
using ProxyWard.Core.JsonRpc;
using ProxyWard.Core.Policies;

namespace ProxyWard.Core.Mcp;

public sealed class McpMessageParser : IMcpMessageParser
{
    public JsonRpcParseResult Parse(ReadOnlyMemory<byte> body, string? contentType)
    {
        if (!IsJsonContentType(contentType))
        {
            return JsonRpcParseResult.UnsupportedContentType();
        }

        JsonNode? root;

        try
        {
            var reader = new Utf8JsonReader(body.Span);
            root = JsonNode.Parse(ref reader);
        }
        catch (JsonException)
        {
            return JsonRpcParseResult.Malformed(PolicyReasonCodes.JsonMalformed);
        }

        return root switch
        {
            JsonObject jsonObject => JsonRpcParseResult.Parsed(
                [CreateMessage(jsonObject, batchIndex: 0)],
                isBatch: false),
            JsonArray jsonArray => ParseBatch(jsonArray),
            _ => JsonRpcParseResult.Malformed(PolicyReasonCodes.JsonMalformed)
        };
    }

    private static JsonRpcParseResult ParseBatch(JsonArray batch)
    {
        if (batch.Count == 0)
        {
            return JsonRpcParseResult.Malformed(PolicyReasonCodes.JsonMalformed);
        }

        var messages = new List<JsonRpcMessage>(batch.Count);

        for (var index = 0; index < batch.Count; index++)
        {
            if (batch[index] is not JsonObject jsonObject)
            {
                return JsonRpcParseResult.Malformed(PolicyReasonCodes.JsonMalformed);
            }

            messages.Add(CreateMessage(jsonObject, index));
        }

        return JsonRpcParseResult.Parsed(messages, isBatch: true);
    }

    private static JsonRpcMessage CreateMessage(JsonObject jsonObject, int batchIndex) =>
        new(
            GetString(jsonObject, "jsonrpc"),
            Clone(jsonObject["id"]),
            GetString(jsonObject, "method"),
            Clone(jsonObject["params"]),
            batchIndex);

    private static string? GetString(JsonObject jsonObject, string propertyName) =>
        jsonObject.TryGetPropertyValue(propertyName, out var value)
            && value is JsonValue jsonValue
            && jsonValue.TryGetValue<string>(out var text)
                ? text
                : null;

    private static JsonNode? Clone(JsonNode? node) =>
        node?.DeepClone();

    private static bool IsJsonContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return false;
        }

        var separatorIndex = contentType.IndexOf(';', StringComparison.Ordinal);
        var mediaType = separatorIndex >= 0
            ? contentType[..separatorIndex]
            : contentType;

        mediaType = mediaType.Trim();

        return mediaType.Equals(MediaTypeNames.Application.Json, StringComparison.OrdinalIgnoreCase)
            || mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase);
    }
}
