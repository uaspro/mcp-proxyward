namespace ProxyWard.Locking.Tools;

public sealed record ToolFingerprint(
    string? NameHash,
    string? TitleHash,
    string? DescriptionHash,
    string? InputSchemaHash,
    string? OutputSchemaHash);
