using System.Text.Json.Nodes;

namespace ProxyWard.Core.JsonRpc;

public sealed record JsonRpcMessage(
    string? JsonRpc,
    JsonNode? Id,
    string? Method,
    JsonNode? Params,
    int BatchIndex);
