using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using ProxyWard.Audit.Events;
using ProxyWard.Audit.Sinks;
using ProxyWard.Locking.Persistence;
using ProxyWard.Locking.Tools;
using ProxyWard.Policy.Persistence;
using ManagementProgram = ProxyWard.Management.Api.Program;

namespace ProxyWard.IntegrationTests;

public class ManagementToolInventoryEndpointTests : IAsyncLifetime
{
    private const string PersistenceDbEnv = "PROXYWARD_DB_PATH";
    private const string AdminTokenEnv = "PROXYWARD_MANAGEMENT_ADMIN_TOKEN";

    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        $"proxyward-management-tools-{Guid.NewGuid():N}.db");

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable(PersistenceDbEnv, null);
        Environment.SetEnvironmentVariable(AdminTokenEnv, null);
        TestFiles.DeleteSqlite(_databasePath);

        return Task.CompletedTask;
    }

    [Fact]
    public async Task ToolsEndpointReturnsLatestKnownToolsAndDriftStatus()
    {
        await SeedSchemaHistoryAsync();
        await SeedPendingDriftAsync("alpha", "repos.search");
        Environment.SetEnvironmentVariable(PersistenceDbEnv, _databasePath);

        await using var factory = new WebApplicationFactory<ManagementProgram>();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/tools");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var payload = await TestJson.ReadAsync(response);
        var servers = payload.RootElement.GetProperty("servers").EnumerateArray().ToArray();

        var alpha = Assert.Single(servers, server => server.GetProperty("serverId").GetString() == "alpha");
        Assert.Equal(2, alpha.GetProperty("latestVersion").GetInt32());
        Assert.Equal("pending", alpha.GetProperty("driftStatus").GetString());

        var alphaTools = alpha.GetProperty("tools").EnumerateArray().ToArray();
        Assert.Equal(["issues.read", "repos.search"], alphaTools.Select(tool => tool.GetProperty("name").GetString()!).ToArray());
        Assert.All(alphaTools, tool => Assert.Equal(2, tool.GetProperty("latestVersion").GetInt32()));

        var search = Assert.Single(alphaTools, tool => tool.GetProperty("name").GetString() == "repos.search");
        Assert.Equal("pending", search.GetProperty("driftStatus").GetString());
        Assert.Equal("Search repositories", search.GetProperty("title").GetString());
        Assert.Equal("sha256:name-repos-search-v2", search.GetProperty("nameHash").GetString());
        Assert.Equal("sha256:input-repos-search-v2", search.GetProperty("inputSchemaHash").GetString());

        var beta = Assert.Single(servers, server => server.GetProperty("serverId").GetString() == "beta");
        Assert.Equal(1, beta.GetProperty("latestVersion").GetInt32());
        Assert.Equal("clean", beta.GetProperty("driftStatus").GetString());
        Assert.Equal("clean", beta.GetProperty("tools")[0].GetProperty("driftStatus").GetString());
    }

    [Fact]
    public async Task ToolsEndpointIncludesConfiguredServersWithoutSchemaHistory()
    {
        await SeedSchemaHistoryAsync();
        await new SqlitePolicyStore(_databasePath).SaveAsync(PolicyYamlWithUnobservedServer());
        Environment.SetEnvironmentVariable(PersistenceDbEnv, _databasePath);

        await using var factory = new WebApplicationFactory<ManagementProgram>();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/tools");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var payload = await TestJson.ReadAsync(response);
        var servers = payload.RootElement.GetProperty("servers").EnumerateArray().ToArray();

        var unobserved = Assert.Single(servers, server => server.GetProperty("serverId").GetString() == "unobserved");
        Assert.Equal(JsonValueKind.Null, unobserved.GetProperty("latestVersion").ValueKind);
        Assert.Equal("unobserved", unobserved.GetProperty("driftStatus").GetString());
        Assert.Empty(unobserved.GetProperty("tools").EnumerateArray());
    }

    [Fact]
    public async Task ToolsEndpointReturnsEmptyInventoryWhenSchemaTablesAreMissing()
    {
        await EnsurePersistenceDbExistsWithoutSchemaLockAsync();
        Environment.SetEnvironmentVariable(PersistenceDbEnv, _databasePath);

        await using var factory = new WebApplicationFactory<ManagementProgram>();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/tools");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var payload = await TestJson.ReadAsync(response);
        Assert.Empty(payload.RootElement.GetProperty("servers").EnumerateArray());
    }

    [Fact]
    public async Task DiscoverEndpointCallsToolsListAndPersistsInventory()
    {
        await using var upstream = await StartToolListUpstreamAsync();
        Environment.SetEnvironmentVariable(PersistenceDbEnv, _databasePath);
        Environment.SetEnvironmentVariable(AdminTokenEnv, "test-admin-token");

        await using var factory = new WebApplicationFactory<ManagementProgram>();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/tools/discover")
        {
            Content = JsonContent.Create(new
            {
                serverId = "gamma",
                upstream = $"{upstream.BaseAddress}/mcp"
            })
        };
        request.Headers.Authorization = new("Bearer", "test-admin-token");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var discoveryPayload = await TestJson.ReadAsync(response);
        Assert.Equal("gamma", discoveryPayload.RootElement.GetProperty("serverId").GetString());
        Assert.Equal(2, discoveryPayload.RootElement.GetProperty("tools").GetArrayLength());
        Assert.Equal("repos.search", discoveryPayload.RootElement.GetProperty("tools")[1].GetProperty("name").GetString());

        using var inventoryResponse = await client.GetAsync("/api/tools");
        Assert.Equal(HttpStatusCode.OK, inventoryResponse.StatusCode);

        using var inventoryPayload = await TestJson.ReadAsync(inventoryResponse);
        var gamma = Assert.Single(
            inventoryPayload.RootElement.GetProperty("servers").EnumerateArray(),
            server => server.GetProperty("serverId").GetString() == "gamma");
        Assert.Equal(1, gamma.GetProperty("latestVersion").GetInt32());
        Assert.Equal(["issues.list", "repos.search"], gamma.GetProperty("tools").EnumerateArray()
            .Select(tool => tool.GetProperty("name").GetString()!)
            .ToArray());
    }

    [Fact]
    public async Task DiscoverEndpointAcceptsEventStreamAndPreservesUpstreamQuery()
    {
        await using var upstream = await StartHuggingFaceStyleToolListUpstreamAsync();
        Environment.SetEnvironmentVariable(PersistenceDbEnv, _databasePath);
        Environment.SetEnvironmentVariable(AdminTokenEnv, "test-admin-token");

        await using var factory = new WebApplicationFactory<ManagementProgram>();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/tools/discover")
        {
            Content = JsonContent.Create(new
            {
                serverId = "huggingface-co",
                upstream = $"{upstream.BaseAddress}/mcp?login&gradio=none"
            })
        };
        request.Headers.Authorization = new("Bearer", "test-admin-token");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var discoveryPayload = await TestJson.ReadAsync(response);
        var tools = discoveryPayload.RootElement.GetProperty("tools").EnumerateArray().ToArray();

        Assert.Equal(8, tools.Length);
        Assert.Equal("hf.tool01", tools[0].GetProperty("name").GetString());
        Assert.Equal("hf.tool08", tools[7].GetProperty("name").GetString());
    }

    [Fact]
    public async Task DiscoverEndpointInitializesSessionBeforeToolsList()
    {
        await using var upstream = await StartSessionRequiredToolListUpstreamAsync();
        Environment.SetEnvironmentVariable(PersistenceDbEnv, _databasePath);
        Environment.SetEnvironmentVariable(AdminTokenEnv, "test-admin-token");

        await using var factory = new WebApplicationFactory<ManagementProgram>();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/tools/discover")
        {
            Content = JsonContent.Create(new
            {
                serverId = "huggingface-co",
                upstream = $"{upstream.BaseAddress}/mcp"
            })
        };
        request.Headers.Authorization = new("Bearer", "test-admin-token");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var discoveryPayload = await TestJson.ReadAsync(response);
        Assert.Equal(["hf_whoami", "space_search"], discoveryPayload.RootElement
            .GetProperty("tools")
            .EnumerateArray()
            .Select(tool => tool.GetProperty("name").GetString()!)
            .ToArray());
    }

    [Fact]
    public async Task DiscoverEndpointPersistsInventoryWhenReadOnlySharedCacheConnectionExists()
    {
        await EnsurePersistenceDbExistsWithoutSchemaLockAsync();
        await using var readOnlyConnection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared
        }.ToString());
        await readOnlyConnection.OpenAsync();

        await using var upstream = await StartToolListUpstreamAsync();
        Environment.SetEnvironmentVariable(PersistenceDbEnv, _databasePath);
        Environment.SetEnvironmentVariable(AdminTokenEnv, "test-admin-token");

        await using var factory = new WebApplicationFactory<ManagementProgram>();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/tools/discover")
        {
            Content = JsonContent.Create(new
            {
                serverId = "gamma",
                upstream = $"{upstream.BaseAddress}/mcp"
            })
        };
        request.Headers.Authorization = new("Bearer", "test-admin-token");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, await CountRowsForServerAsync(_databasePath, "gamma"));
    }

    private async Task SeedSchemaHistoryAsync()
    {
        using var store = new SqliteTrackedToolSchemaStore(_databasePath);
        var capturedAt = new DateTimeOffset(2026, 5, 10, 9, 0, 0, TimeSpan.Zero);

        await store.RecordAsync(
            CreateSnapshot("alpha", "https://alpha.example/", "2025-11-25", [
                CreateEntry("repos.search", "v1", "Search repositories", "Search old repositories")
            ]),
            capturedAt,
            CancellationToken.None);

        await store.RecordAsync(
            CreateSnapshot("alpha", "https://alpha.example/", "2025-11-25", [
                CreateEntry("issues.read", "v2", "Read issues", "Read issues"),
                CreateEntry("repos.search", "v2", "Search repositories", "Search accessible repositories")
            ]),
            capturedAt.AddMinutes(5),
            CancellationToken.None);

        await store.RecordAsync(
            CreateSnapshot("beta", "https://beta.example/", "2025-11-25", [
                CreateEntry("files.list", "v1", "List files", "List files")
            ]),
            capturedAt.AddMinutes(10),
            CancellationToken.None);
    }

    private async Task SeedPendingDriftAsync(string serverId, string toolName)
    {
        using var store = new SqliteSchemaDriftReviewStore(_databasePath);
        await store.RecordObservationAsync(
            new DriftReviewObservation(
                ServerId: serverId,
                ToolName: toolName,
                FieldName: "schema",
                FromVersion: 1,
                ToVersion: 2,
                Reasons: ["tool_schema_changed"],
                PolicyVersion: "sha256:policy",
                DetectedAtUtc: new DateTimeOffset(2026, 5, 10, 9, 10, 0, TimeSpan.Zero)),
            CancellationToken.None);
    }

    private async Task EnsurePersistenceDbExistsWithoutSchemaLockAsync()
    {
        using var sink = new SqliteAuditSink(_databasePath);
        await sink.WriteAsync(new AuditEvent(
            Timestamp: DateTimeOffset.UtcNow.AddDays(-1),
            EventType: "tool_call_policy",
            Mode: "audit",
            Decision: AuditDecision.Allow,
            ServerId: "alpha",
            Method: "tools/list",
            ToolName: null,
            Reasons: ["allowed"],
            PolicyVersion: "sha256:policy",
            CorrelationId: "corr-tools-test",
            RequestBytes: 0,
            DurationMs: 1,
            ArgumentSummary: null,
            BatchSize: 0), CancellationToken.None);
    }

    private static async Task<long> CountRowsForServerAsync(string databasePath, string serverId)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString());
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM tool_schema_versions WHERE server_id = $server_id;";
        command.Parameters.AddWithValue("$server_id", serverId);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static ToolSchemaSnapshotInput CreateSnapshot(
        string serverId,
        string upstreamUrl,
        string protocol,
        IReadOnlyCollection<ToolSchemaSnapshotEntry> tools) =>
        new(serverId, upstreamUrl, protocol, tools, PolicyVersion: null, SourceCorrelationId: null);

    private static ToolSchemaSnapshotEntry CreateEntry(
        string name,
        string suffix,
        string title,
        string description) =>
        new(
            name,
            new ToolFingerprint(
                NameHash: $"sha256:name-{name.Replace('.', '-')}-{suffix}",
                TitleHash: $"sha256:title-{name.Replace('.', '-')}-{suffix}",
                DescriptionHash: $"sha256:description-{name.Replace('.', '-')}-{suffix}",
                InputSchemaHash: $"sha256:input-{name.Replace('.', '-')}-{suffix}",
                OutputSchemaHash: $"sha256:output-{name.Replace('.', '-')}-{suffix}"),
            Title: title,
            Description: description);

    private static Task<TestUpstream> StartToolListUpstreamAsync() =>
        TestUpstream.StartAsync(context => Results.Json(new
        {
            jsonrpc = "2.0",
            id = "proxyward-dashboard-tools-discovery",
            result = new
            {
                tools = new object[]
                {
                    new
                    {
                        name = "repos.search",
                        title = "Search repositories",
                        description = "Find repositories",
                        inputSchema = new { type = "object" }
                    },
                    new
                    {
                        name = "issues.list",
                        title = "List issues",
                        description = "List issues",
                        inputSchema = new { type = "object" }
                    }
                }
            }
        }).ExecuteAsync(context));

    private static Task<TestUpstream> StartHuggingFaceStyleToolListUpstreamAsync() =>
        TestUpstream.StartAsync(async context =>
        {
            if (!context.Request.Query.ContainsKey("login")
                || context.Request.Query["gradio"] != "none")
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            if (!context.Request.Headers.Accept.ToString().Contains(MediaTypeNames.Text.EventStream, StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = StatusCodes.Status406NotAcceptable;
                return;
            }

            context.Response.ContentType = MediaTypeNames.Text.EventStream;
            await context.Response.WriteAsync(CreateHuggingFaceStyleEventStream()).ConfigureAwait(false);
        });

    private static string CreateHuggingFaceStyleEventStream()
    {
        var toolsJson = string.Join(
            ",",
            Enumerable.Range(1, 8).Select(index =>
                $"{{\"name\":\"hf.tool{index:00}\",\"title\":\"HF Tool {index}\",\"description\":\"Synthetic Hugging Face tool {index}\",\"inputSchema\":{{\"type\":\"object\"}}}}"));

        return string.Join('\n', [
            "event: endpoint",
            "data: /mcp/messages?sessionId=huggingface-co",
            "",
            "event: message",
            $"data: {{\"jsonrpc\":\"2.0\",\"id\":\"proxyward-dashboard-tools-discovery\",\"result\":{{\"tools\":[{toolsJson}]}}}}",
            ""
        ]);
    }

    private static Task<TestUpstream> StartSessionRequiredToolListUpstreamAsync() =>
        TestUpstream.StartAsync(async context =>
        {
            using var requestBody = await JsonDocument.ParseAsync(context.Request.Body).ConfigureAwait(false);
            var method = requestBody.RootElement.GetProperty("method").GetString();

            switch (method)
            {
                case "initialize":
                    context.Response.Headers["Mcp-Session-Id"] = "test-session";
                    await Results.Json(new
                    {
                        jsonrpc = "2.0",
                        id = requestBody.RootElement.GetProperty("id").GetString(),
                        result = new
                        {
                            protocolVersion = "2025-11-25",
                            capabilities = new { tools = new { listChanged = false } }
                        }
                    }).ExecuteAsync(context).ConfigureAwait(false);
                    return;
                case "notifications/initialized":
                    Assert.Equal("test-session", context.Request.Headers["Mcp-Session-Id"].ToString());
                    context.Response.StatusCode = StatusCodes.Status202Accepted;
                    return;
                case "tools/list":
                    if (context.Request.Headers["Mcp-Session-Id"] != "test-session")
                    {
                        await Results.Json(new
                        {
                            jsonrpc = "2.0",
                            id = "proxyward-dashboard-tools-discovery",
                            error = new
                            {
                                code = -32600,
                                message = "Session ID required"
                            }
                        }, statusCode: StatusCodes.Status400BadRequest).ExecuteAsync(context).ConfigureAwait(false);
                        return;
                    }

                    await Results.Json(new
                    {
                        jsonrpc = "2.0",
                        id = "proxyward-dashboard-tools-discovery",
                        result = new
                        {
                            tools = new object[]
                            {
                                new
                                {
                                    name = "hf_whoami",
                                    description = "Hugging Face user info",
                                    inputSchema = new { type = "object" }
                                },
                                new
                                {
                                    name = "space_search",
                                    description = "Find spaces",
                                    inputSchema = new { type = "object" }
                                }
                            }
                        }
                    }).ExecuteAsync(context).ConfigureAwait(false);
                    return;
                default:
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
            }
        });

    private static string PolicyYamlWithUnobservedServer() =>
        """
        mode: audit
        inspection:
          maxBodyBytes: 1048576
          unsupportedStreaming: warn
          batchToolCalls: failClosed
        audit:
          enabled: true
        observability:
          serviceName: mcp-proxyward
          console:
            enabled: true
          otlp:
            enabled: false
            endpoint: http://otel-collector:4317
          applicationInsights:
            enabled: false
            connectionStringEnv: APPLICATIONINSIGHTS_CONNECTION_STRING
          sampling:
            tracesRatio: 1.0
        servers:
          alpha:
            route: /alpha/mcp
            upstream: https://alpha.example/mcp
            allowed: true
            tools:
              default: deny
              allow: []
              block: []
            arguments:
              paths:
                allowedRoots: []
                blockTraversal: false
              hosts:
                allow: []
                blockPrivateNetworks: false
              commands:
                blockShell: false
                dangerous: []
          unobserved:
            route: /unobserved/mcp
            upstream: https://unobserved.example/mcp
            allowed: true
            tools:
              default: deny
              allow: []
              block: []
            arguments:
              paths:
                allowedRoots: []
                blockTraversal: false
              hosts:
                allow: []
                blockPrivateNetworks: false
              commands:
                blockShell: false
                dangerous: []
        """;
}
