using ProxyWard.Core.JsonRpc;

namespace ProxyWard.Core.Mcp;

public interface IMcpMessageParser
{
    JsonRpcParseResult Parse(ReadOnlyMemory<byte> body, string? contentType);
}
