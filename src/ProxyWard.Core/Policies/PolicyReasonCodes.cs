namespace ProxyWard.Core.Policies;

public static class PolicyReasonCodes
{
    public const string ServerNotAllowed = "server_not_allowed";
    public const string JsonMalformed = "json_malformed";
    public const string ToolBlocked = "tool_blocked";
    public const string ToolNotAllowed = "tool_not_allowed";
    public const string BatchBlocked = "batch_blocked";
    public const string ToolDescriptionChanged = "tool_description_changed";
    public const string ToolSchemaChanged = "tool_schema_changed";
    public const string McpProtocolChanged = "mcp_protocol_changed";
    public const string InspectionUnsupported = "inspection_unsupported";
    public const string PathTraversal = "path_traversal";
    public const string PathOutsideAllowedRoots = "path_outside_allowed_roots";
    public const string HostNotAllowed = "host_not_allowed";
    public const string PrivateNetworkTarget = "private_network_target";
    public const string DangerousCommand = "dangerous_command";
}
