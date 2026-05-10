using System.Text.Json.Nodes;

namespace ProxyWard.Api.Middleware;

internal static class JsonRpcPolicyError
{
    public static bool HasSupportedRequestId(JsonNode id)
    {
        var json = id.ToJsonString();
        return json.Length > 0
            && (json[0] == '"' || json[0] == '-' || char.IsDigit(json[0]));
    }

    public static JsonObject Create(
        JsonNode id,
        IReadOnlyCollection<string> reasons,
        string message,
        int? batchIndex = null,
        string? toolName = null)
    {
        var data = new JsonObject
        {
            ["reasons"] = CreateReasonNodes(reasons)
        };

        if (batchIndex is not null)
        {
            data["batchIndex"] = batchIndex.Value;
        }

        if (toolName is not null)
        {
            data["toolName"] = toolName;
        }

        return new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id.DeepClone(),
            ["error"] = new JsonObject
            {
                ["code"] = -32001,
                ["message"] = message,
                ["data"] = data
            }
        };
    }

    private static JsonArray CreateReasonNodes(IReadOnlyCollection<string> reasons)
    {
        var reasonNodes = new JsonArray();
        foreach (var reason in reasons)
        {
            reasonNodes.Add(JsonValue.Create(reason));
        }

        return reasonNodes;
    }
}
