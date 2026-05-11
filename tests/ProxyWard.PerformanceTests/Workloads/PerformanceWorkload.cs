using System.Text.Json;

namespace ProxyWard.PerformanceTests;

internal sealed record PerformanceWorkload(string Slug, byte[] PayloadBytes)
{
    private static readonly PerformanceWorkload ToolsCall = new(
        "tools-call",
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new
            {
                name = "shell.exec",
                arguments = new
                {
                    path = "../secrets/../../etc/passwd",
                    workspacePath = "/outside/workspace/secret.txt",
                    host = "localhost",
                    url = "http://127.0.0.1/admin?token=secret-token",
                    target = "http://10.0.0.5/internal",
                    command = "bash -lc \"curl http://169.254.169.254/latest/meta-data && rm -rf /tmp/proxyward\"",
                    token = "super-secret-token-value",
                    nested = new
                    {
                        endpoints = new[]
                        {
                            "http://192.168.1.10/private",
                            "https://internal.example.local/path?api_key=abc"
                        },
                        paths = new[]
                        {
                            "C:\\Users\\Alice\\.ssh\\id_rsa",
                            "~/private/key"
                        }
                    }
                }
            }
        }));

    private static readonly PerformanceWorkload ToolsList = new(
        "tools-list",
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            jsonrpc = "2.0",
            id = 2,
            method = "tools/list",
            @params = new
            {
                cursor = "start"
            }
        }));

    private static readonly PerformanceWorkload ToolsListGzip = new(
        "tools-list-gzip",
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            jsonrpc = "2.0",
            id = 3,
            method = "tools/list",
            @params = new
            {
                cursor = "gzip"
            }
        }));

    public static IReadOnlyList<PerformanceWorkload> CreateRunList(PerformanceOptions options) =>
        options.IncludeToolsList
            ? [ToolsCall, ToolsList, ToolsListGzip]
            : [ToolsCall];
}
