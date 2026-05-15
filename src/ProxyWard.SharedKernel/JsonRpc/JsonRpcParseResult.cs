namespace ProxyWard.Core.JsonRpc;

public sealed record JsonRpcParseResult(
    JsonRpcParseStatus Status,
    IReadOnlyList<JsonRpcMessage> Messages,
    IReadOnlyCollection<string> Reasons,
    bool IsBatch)
{
    public static JsonRpcParseResult Parsed(
        IReadOnlyList<JsonRpcMessage> messages,
        bool isBatch) =>
        new(JsonRpcParseStatus.Parsed, messages, [], isBatch);

    public static JsonRpcParseResult Malformed(params string[] reasons) =>
        new(JsonRpcParseStatus.Malformed, [], reasons, IsBatch: false);

    public static JsonRpcParseResult UnsupportedContentType() =>
        new(JsonRpcParseStatus.UnsupportedContentType, [], [], IsBatch: false);
}

public enum JsonRpcParseStatus
{
    Parsed,
    Malformed,
    UnsupportedContentType
}
