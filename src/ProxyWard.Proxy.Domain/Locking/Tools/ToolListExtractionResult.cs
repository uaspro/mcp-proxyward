namespace ProxyWard.Locking.Tools;

public sealed record ToolListExtractionResult(
    bool Success,
    IReadOnlyList<DiscoveredTool> Tools,
    IReadOnlyCollection<string> Reasons,
    bool Skipped = false,
    string? SkipReason = null)
{
    public static ToolListExtractionResult Extracted(IReadOnlyList<DiscoveredTool> tools) =>
        new(Success: true, tools, []);

    public static ToolListExtractionResult Failed(params string[] reasons) =>
        new(Success: false, [], reasons);

    public static ToolListExtractionResult SkippedInspection(string reason) =>
        new(Success: false, [], [], Skipped: true, SkipReason: reason);
}
