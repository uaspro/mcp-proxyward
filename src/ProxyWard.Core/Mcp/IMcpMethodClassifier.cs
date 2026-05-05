using ProxyWard.Core.JsonRpc;

namespace ProxyWard.Core.Mcp;

public interface IMcpMethodClassifier
{
    McpMessageClassification Classify(JsonRpcMessage message);
}
