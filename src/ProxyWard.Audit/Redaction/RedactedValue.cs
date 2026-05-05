using System.Text.Json.Nodes;

namespace ProxyWard.Audit.Redaction;

public sealed record RedactedValue(JsonNode? Value, bool WasRedacted);
