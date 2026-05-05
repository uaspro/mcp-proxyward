namespace ProxyWard.Core.Mcp;

public sealed record McpMessageClassification(
    McpMessageKind Kind,
    string? ToolName);

public enum McpMessageKind
{
    Other,
    ToolsList,
    ToolCall
}
