using System.Text.Json.Nodes;

namespace ProxyWard.Locking.Tools;

public sealed record DiscoveredTool(
    string? Name,
    string? Title,
    string? Description,
    JsonNode? InputSchema,
    JsonNode? OutputSchema);
