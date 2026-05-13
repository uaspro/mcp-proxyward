namespace ProxyWard.Api.Middleware;

internal sealed record FilteredToolsListResponse(byte[] Body, string ContentType, string? ContentEncoding);
