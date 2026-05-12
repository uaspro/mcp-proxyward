using ProxyWard.Core.JsonRpc;
using ProxyWard.Core.Mcp;
using ProxyWard.Policy.Configuration;

namespace ProxyWard.Api.Middleware;

public sealed class ResponseInspectionTargetResolver(IMcpMethodClassifier classifier)
{
    internal ResponseInspectionTarget Resolve(HttpContext context, ServerPolicy server)
    {
        if (!context.Items.TryGetValue(RequestInspectionItems.JsonRpcParseResult, out var parseItem)
            || parseItem is not JsonRpcParseResult parseResult
            || parseResult.Status != JsonRpcParseStatus.Parsed)
        {
            return ResponseInspectionTarget.None;
        }

        foreach (var message in parseResult.Messages)
        {
            var classification = classifier.Classify(message);
            if (classification.Kind == McpMessageKind.ToolsList)
            {
                return ResponseInspectionTarget.ToolsList;
            }
        }

        if (!server.Secrets.BlockReturn || server.Secrets.Patterns.Count == 0)
        {
            return ResponseInspectionTarget.None;
        }

        foreach (var message in parseResult.Messages)
        {
            var classification = classifier.Classify(message);
            if (classification.Kind == McpMessageKind.ToolCall)
            {
                return new ResponseInspectionTarget(
                    ResponseInspectionKind.ToolCallSecretReturn,
                    "tools/call",
                    ResponseInspectionEventTypes.ToolResponseSecretInspection,
                    classification.ToolName,
                    parseResult,
                    message);
            }
        }

        return ResponseInspectionTarget.None;
    }
}
