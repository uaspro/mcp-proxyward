namespace ProxyWard.Api.Control;

public sealed class YarpConfigApplyRequest
{
    public List<YarpRouteApplyRequest>? Routes { get; set; }

    public List<YarpClusterApplyRequest>? Clusters { get; set; }
}

public sealed class YarpRouteApplyRequest
{
    public string? RouteId { get; set; }

    public string? ClusterId { get; set; }

    public int? Order { get; set; }

    public YarpRouteMatchApplyRequest? Match { get; set; }

    public List<Dictionary<string, string>>? Transforms { get; set; }
}

public sealed class YarpRouteMatchApplyRequest
{
    public string? Path { get; set; }
}

public sealed class YarpClusterApplyRequest
{
    public string? ClusterId { get; set; }

    public Dictionary<string, YarpDestinationApplyRequest>? Destinations { get; set; }
}

public sealed class YarpDestinationApplyRequest
{
    public string? Address { get; set; }
}

public sealed class ModeApplyRequest
{
    public string? Mode { get; set; }
}
