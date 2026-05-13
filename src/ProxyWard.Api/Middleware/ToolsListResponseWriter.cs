using ProxyWard.Policy.Configuration;

namespace ProxyWard.Api.Middleware;

internal static class ToolsListResponseWriter
{
    public static FilteredToolsListResponse? TryCreateVisibleResponse(
        HttpContext context,
        ServerPolicy server,
        ProxyWardPolicy policy,
        byte[] body)
    {
        var hiddenToolNames = ToolsListResponseFilter.CreateToolNameSet(server.Tools.Hide);
        var hideDefaultTools = server.Tools.Default == ToolDefaultMode.Hide;
        if (hiddenToolNames.Count == 0 && !hideDefaultTools)
        {
            return null;
        }

        var visibleToolNames = hideDefaultTools
            ? CreateDefaultHideVisibleToolNameSet(server)
            : [];

        return ToolsListResponseFilter.TryCreateFilteredResponse(
            body,
            context.Response.ContentType,
            context.Response.Headers["Content-Encoding"].ToString(),
            policy.Inspection.MaxBodyBytes,
            toolName => hiddenToolNames.Contains(toolName)
                || (hideDefaultTools && !visibleToolNames.Contains(toolName)),
            out var filteredResponse)
                ? filteredResponse
                : null;
    }

    public static HashSet<string> CreateHiddenAwareToolNameSet(
        ServerPolicy server,
        IEnumerable<string?> toolNames)
    {
        var result = ToolsListResponseFilter.CreateToolNameSet(server.Tools.Hide);
        result.UnionWith(ToolsListResponseFilter.CreateToolNameSet(toolNames));
        return result;
    }

    private static HashSet<string> CreateDefaultHideVisibleToolNameSet(ServerPolicy server)
    {
        var result = ToolsListResponseFilter.CreateToolNameSet(server.Tools.Allow);
        result.UnionWith(ToolsListResponseFilter.CreateToolNameSet(server.Tools.Block));
        return result;
    }

    public static async Task WriteVisibleOrOriginalAsync(
        HttpContext context,
        ServerPolicy server,
        ProxyWardPolicy policy,
        byte[] body,
        ResponseInspectionStream capture,
        Stream destination,
        FilteredToolsListResponse? prefilteredResponse = null)
    {
        var response = prefilteredResponse ?? TryCreateVisibleResponse(context, server, policy, body);
        if (response is null)
        {
            await capture.CopyBufferedBodyToAsync(destination, context.RequestAborted);
            return;
        }

        await WriteFilteredAsync(context, destination, response);
    }

    public static async Task WriteFilteredAsync(
        HttpContext context,
        Stream destination,
        FilteredToolsListResponse response)
    {
        context.Response.ContentType = response.ContentType;
        context.Response.ContentLength = response.Body.Length;
        if (string.IsNullOrWhiteSpace(response.ContentEncoding))
        {
            context.Response.Headers.Remove("Content-Encoding");
        }
        else
        {
            context.Response.Headers.ContentEncoding = response.ContentEncoding;
        }

        context.Response.Headers.Remove("ETag");
        context.Response.Headers.Remove("Content-MD5");

        await destination.WriteAsync(response.Body, context.RequestAborted);
    }
}
