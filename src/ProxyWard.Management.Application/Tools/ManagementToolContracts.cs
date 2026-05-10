namespace ProxyWard.Management.Application.Tools;

public sealed record ManagementToolInventoryResponse(
    IReadOnlyList<ManagementToolInventoryServer> Servers);

public sealed record ManagementToolInventoryServer(
    string ServerId,
    int? LatestVersion,
    string DriftStatus,
    IReadOnlyList<ManagementToolInventoryTool> Tools);

public sealed record ManagementToolInventoryTool(
    string Name,
    int LatestVersion,
    string DriftStatus,
    string? Title,
    string? Description,
    string? NameHash,
    string? TitleHash,
    string? DescriptionHash,
    string? InputSchemaHash,
    string? OutputSchemaHash);

public sealed record ManagementToolDiscoveryRequest(
    string? ServerId,
    string? Upstream);

public sealed record ManagementToolDiscoveryResponse(
    string ServerId,
    string Upstream,
    int LatestVersion,
    string SnapshotHash,
    bool WasNewVersion,
    IReadOnlyList<ManagementToolInventoryTool> Tools);

public interface IManagementToolInventoryRepository
{
    Task<ManagementToolInventoryResponse> GetAsync(CancellationToken cancellationToken);
}

public interface IManagementToolDiscoveryService
{
    Task<ManagementToolDiscoveryResponse> DiscoverAsync(
        ManagementToolDiscoveryRequest? request,
        CancellationToken cancellationToken);
}

public sealed class ManagementToolDiscoveryRequestException : Exception
{
    public ManagementToolDiscoveryRequestException(string error, string message)
        : base(message)
    {
        Error = error;
    }

    public string Error { get; }
}

public sealed class ManagementToolDiscoveryException : Exception
{
    public ManagementToolDiscoveryException(
        string error,
        string message,
        int? upstreamStatusCode = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Error = error;
        UpstreamStatusCode = upstreamStatusCode;
    }

    public string Error { get; }

    public int? UpstreamStatusCode { get; }
}
