using System.Text.Json.Nodes;
using ProxyWard.Core.JsonRpc;

namespace ProxyWard.Core.Mcp;

public sealed class McpMethodClassifier : IMcpMethodClassifier
{
    public McpMessageClassification Classify(JsonRpcMessage message) =>
        message.Method switch
        {
            "tools/call" => new McpMessageClassification(
                McpMessageKind.ToolCall,
                ExtractToolName(message.Params)),
            "tools/list" => new McpMessageClassification(
                McpMessageKind.ToolsList,
                ToolName: null),
            _ => new McpMessageClassification(
                McpMessageKind.Other,
                ToolName: null)
        };

    private static string? ExtractToolName(JsonNode? parameters)
    {
        if (parameters is not JsonObject parameterObject
            || !parameterObject.TryGetPropertyValue("name", out var nameNode)
            || nameNode is not JsonValue nameValue
            || !nameValue.TryGetValue<string>(out var toolName))
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(toolName) ? null : toolName;
    }
}
