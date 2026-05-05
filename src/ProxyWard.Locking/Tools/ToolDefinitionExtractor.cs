using System.Text.Json;
using System.Text.Json.Nodes;
using ProxyWard.Core.Policies;

namespace ProxyWard.Locking.Tools;

public sealed class ToolDefinitionExtractor : IToolDefinitionExtractor
{
    public ToolListExtractionResult Extract(ReadOnlyMemory<byte> responseBody)
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
}
