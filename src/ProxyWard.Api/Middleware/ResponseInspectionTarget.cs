using ProxyWard.Core.JsonRpc;

namespace ProxyWard.Api.Middleware;

internal static class ResponseInspectionEventTypes
{
    public const string ToolsListResponseInspection = "tools_list_response_inspection";
    public const string ToolResponseSecretInspection = "tool_response_secret_inspection";
}

internal enum ResponseInspectionKind
{
    None,
    ToolsList,
    ToolCallSecretReturn
}

internal sealed record ResponseInspectionTarget(
    ResponseInspectionKind Kind,
    string Method,
    string EventType,
    string? ToolName,
    JsonRpcParseResult? ParseResult,
    JsonRpcMessage? Message)
{
    public static ResponseInspectionTarget None { get; } = new(
        ResponseInspectionKind.None,
        string.Empty,
        string.Empty,
        null,
        null,
        null);

    public static ResponseInspectionTarget ToolsList { get; } = new(
        ResponseInspectionKind.ToolsList,
        "tools/list",
        ResponseInspectionEventTypes.ToolsListResponseInspection,
        null,
        null,
        null);
}
