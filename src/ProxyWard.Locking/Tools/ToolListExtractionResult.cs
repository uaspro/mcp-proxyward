namespace ProxyWard.Locking.Tools;

public sealed record ToolListExtractionResult(
    bool Success,
    IReadOnlyList<DiscoveredTool> Tools,
    IReadOnlyCollection<string> Reasons)
{
    public static ToolListExtractionResult Extracted(IReadOnlyList<DiscoveredTool> tools) =>
        new(Success: true, tools, []);

    public static ToolListExtractionResult Failed(params string[] reasons) =>
        new(Success: false, [], reasons);
}
