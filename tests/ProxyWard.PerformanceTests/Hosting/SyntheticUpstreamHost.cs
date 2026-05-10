using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;

namespace ProxyWard.PerformanceTests;

internal static class SyntheticUpstreamHost
{
    public const int ToolCount = 50;

    private static readonly string ToolCallJson = JsonSerializer.Serialize(new
    {
        jsonrpc = "2.0",
        id = 1,
        result = new
        {
            content = new[]
            {
                new
                {
                    type = "text",
                    text = "ok"
                }
            }
        }
    });

    public static async Task<StartedHost> StartAsync()
    {
        var builder = PerformanceHostFactory.CreateBuilder();
        var toolsListJson = CreateToolsListResponse();

        var app = builder.Build();
        app.MapPost("/mcp", async context =>
        {
            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
            var body = await reader.ReadToEndAsync(context.RequestAborted).ConfigureAwait(false);
            var responseJson = body.Contains("\"tools/list\"", StringComparison.Ordinal)
                ? toolsListJson
                : ToolCallJson;

            await PerformanceHostFactory
                .WriteJsonWithContentLengthAsync(context, responseJson)
                .ConfigureAwait(false);
        });

        await app.StartAsync().ConfigureAwait(false);
        return new StartedHost(PerformanceHostFactory.GetBoundAddress(app), app);
    }

    private static string CreateToolsListResponse()
    {
        var tools = Enumerable.Range(0, ToolCount)
            .Select(index => new
            {
                name = $"tool_{index:000}",
                title = $"Tool {index:000}",
                description = $"Synthetic performance tool {index:000} with a moderately sized description.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        path = new { type = "string" },
                        host = new { type = "string" },
                        command = new { type = "string" },
                        metadata = new
                        {
                            type = "object",
                            additionalProperties = true
                        }
                    },
                    required = new[] { "path", "host" }
                },
                outputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        ok = new { type = "boolean" }
                    }
                }
            })
            .ToArray();

        return JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 2,
            result = new
            {
                tools
            }
        });
    }
}
