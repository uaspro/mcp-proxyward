using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ProxyWard.Api.Observability;
using ProxyWard.Core.Policies;
using ProxyWard.Locking.Persistence;
using ProxyWard.Locking.Tools;

namespace ProxyWard.IntegrationTests;

public class ToolsListResponseInspectionIntegrationTests
{
    [Fact]
    public async Task InspectableToolsListJsonResponseIsReturnedUnchangedAndAudited()
    {
        const string responseBody = """
            {"jsonrpc":"2.0","id":1,"result":{"tools":[{"name":"repos.search","title":"Search","description":"Find repositories","inputSchema":{"type":"object"}},{"name":"issues.list","description":"List issues","inputSchema":{"type":"object"}}]}}
            """;
        await using var upstream = await StartUpstreamAsync(ctx => WriteResponseAsync(ctx, responseBody, "application/json"));
        var dbPath = NewTempSqlitePath();
        var policyPath = WriteTempPolicy(CreatePolicy("audit", "warn", 4096, upstream.BaseAddress, dbPath));
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", policyPath);

        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();

            using var response = await client.PostAsync(
                "/github/mcp",
                new StringContent(ToolsListRequest, Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(responseBody, await response.Content.ReadAsStringAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", null);
        }

        var rows = ReadAuditEvents(dbPath);
        var row = Assert.Single(rows);
        Assert.Equal("tools_list_response_inspection", row.EventType);
        Assert.Equal("allow", row.Decision);
        Assert.Equal("tools/list", row.Method);
        Assert.Equal(string.Empty, row.Reasons);

        using var payload = JsonDocument.Parse(row.PayloadJson);
        var summary = payload.RootElement.GetProperty("argumentSummary");
        Assert.Equal(2, summary.GetProperty("toolCount").GetInt32());
        var toolNames = summary.GetProperty("toolNames").EnumerateArray().Select(name => name.GetString()!).ToArray();
        Assert.Equal(["issues.list", "repos.search"], toolNames);

        DeleteIfExists(dbPath);
    }

    [Fact]
    public async Task EventStreamToolsListResponseIsInspectedAndAudited()
    {
        const string responseBody = """
            event: message
            data: {"jsonrpc":"2.0","id":1,"result":{"tools":[{"name":"repos.search","title":"Search","description":"Find repositories","inputSchema":{"type":"object"}},{"name":"issues.list","description":"List issues","inputSchema":{"type":"object"}}]}}

            """;
        await using var upstream = await StartUpstreamAsync(ctx => WriteResponseAsync(ctx, responseBody, "text/event-stream", setLength: false));
        var dbPath = NewTempSqlitePath();
        var policyPath = WriteTempPolicy(CreatePolicy("enforce", "block", 4096, upstream.BaseAddress, dbPath));
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", policyPath);

        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();

            using var response = await client.PostAsync(
                "/github/mcp",
                new StringContent(ToolsListRequest, Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(responseBody, await response.Content.ReadAsStringAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", null);
        }

        var row = Assert.Single(ReadAuditEvents(dbPath), r => r.EventType == "tools_list_response_inspection");
        Assert.Equal("allow", row.Decision);
        Assert.Equal(string.Empty, row.Reasons);

        using var payload = JsonDocument.Parse(row.PayloadJson);
        var summary = payload.RootElement.GetProperty("argumentSummary");
        Assert.Equal(2, summary.GetProperty("toolCount").GetInt32());
        Assert.Equal(["issues.list", "repos.search"], summary.GetProperty("toolNames")
            .EnumerateArray()
            .Select(name => name.GetString()!)
            .ToArray());

        DeleteIfExists(dbPath);
    }

    [Fact]
    public async Task GzipEncodedToolsListJsonResponseIsDecodedForInspection()
    {
        const string responseBody = """
            {"jsonrpc":"2.0","id":1,"result":{"tools":[{"name":"hf.whoami","title":"Hugging Face User Info","description":"Show user info","inputSchema":{"type":"object"}},{"name":"space.search","title":"Space Search","description":"Find spaces","inputSchema":{"type":"object"}}]}}
            """;
        await using var upstream = await StartUpstreamAsync(context => WriteCompressedResponseAsync(context, responseBody, "application/json", "gzip"));
        var dbPath = NewTempSqlitePath();
        var policyPath = WriteTempPolicy(CreatePolicy("audit", "warn", 4096, upstream.BaseAddress, dbPath));
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", policyPath);

        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();

            using var response = await client.PostAsync(
                "/github/mcp",
                new StringContent(ToolsListRequest, Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", null);
        }

        var row = Assert.Single(ReadAuditEvents(dbPath));
        Assert.Equal("tools_list_response_inspection", row.EventType);
        Assert.Equal("allow", row.Decision);
        Assert.Equal(string.Empty, row.Reasons);

        using var payload = JsonDocument.Parse(row.PayloadJson);
        var summary = payload.RootElement.GetProperty("argumentSummary");
        Assert.Equal(2, summary.GetProperty("toolCount").GetInt32());
        Assert.Equal(["hf.whoami", "space.search"], summary.GetProperty("toolNames")
            .EnumerateArray()
            .Select(name => name.GetString()!)
            .ToArray());

        DeleteIfExists(dbPath);
    }

    [Fact]
    public async Task EventStreamToolsListResponseIgnoresEndpointEventAndInspectsMessage()
    {
        var toolJson = string.Join(
            ",",
            Enumerable.Range(1, 8).Select(index =>
                $"{{\"name\":\"hf.tool{index:00}\",\"title\":\"HF Tool {index}\",\"description\":\"Synthetic Hugging Face tool {index}\",\"inputSchema\":{{\"type\":\"object\"}}}}"));
        var responseBody = string.Join('\n', [
            "event: endpoint",
            "data: /mcp/messages?sessionId=huggingface-co",
            "",
            "event: message",
            $"data: {{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{{\"tools\":[{toolJson}]}}}}",
            ""
        ]);
        await using var upstream = await StartUpstreamAsync(ctx => WriteResponseAsync(ctx, responseBody, "text/event-stream", setLength: false));
        var dbPath = NewTempSqlitePath();
        var policyPath = WriteTempPolicy(CreatePolicy("enforce", "block", 8192, upstream.BaseAddress, dbPath));
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", policyPath);

        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();

            using var response = await client.PostAsync(
                "/github/mcp",
                new StringContent(ToolsListRequest, Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(responseBody, await response.Content.ReadAsStringAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", null);
        }

        var row = Assert.Single(ReadAuditEvents(dbPath), r => r.EventType == "tools_list_response_inspection");
        Assert.Equal("allow", row.Decision);
        Assert.Equal(string.Empty, row.Reasons);

        using var payload = JsonDocument.Parse(row.PayloadJson);
        var summary = payload.RootElement.GetProperty("argumentSummary");
        Assert.Equal(8, summary.GetProperty("toolCount").GetInt32());
        Assert.Equal(
            Enumerable.Range(1, 8).Select(index => $"hf.tool{index:00}").ToArray(),
            summary.GetProperty("toolNames").EnumerateArray().Select(name => name.GetString()!).ToArray());

        DeleteIfExists(dbPath);
    }

    [Fact]
    public async Task EventStreamToolsListResponseWithOnlyEndpointEventIsPassedThroughWithoutMalformedWarning()
    {
        const string responseBody = """
            event: endpoint
            data: /mcp/messages?sessionId=huggingface-co

            """;
        await using var upstream = await StartUpstreamAsync(ctx => WriteResponseAsync(ctx, responseBody, "text/event-stream", setLength: false));
        var dbPath = NewTempSqlitePath();
        var policyPath = WriteTempPolicy(CreatePolicy("audit", "warn", 4096, upstream.BaseAddress, dbPath));
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", policyPath);

        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();

            using var response = await client.PostAsync(
                "/github/mcp",
                new StringContent(ToolsListRequest, Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(responseBody, await response.Content.ReadAsStringAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", null);
        }

        var row = Assert.Single(ReadAuditEvents(dbPath));
        Assert.Equal("tools_list_response_inspection", row.EventType);
        Assert.Equal("allow", row.Decision);
        Assert.Equal(string.Empty, row.Reasons);
        Assert.DoesNotContain("json_malformed", row.PayloadJson, StringComparison.Ordinal);

        using var payload = JsonDocument.Parse(row.PayloadJson);
        var summary = payload.RootElement.GetProperty("argumentSummary");
        Assert.True(summary.GetProperty("inspectionSkipped").GetBoolean());
        Assert.Equal("event_stream_without_jsonrpc_message", summary.GetProperty("inspectionSkipReason").GetString());

        DeleteIfExists(dbPath);
    }

    [Fact]
    public async Task EmptyToolsListResponseIsPassedThroughWithoutMalformedWarning()
    {
        await using var upstream = await StartUpstreamAsync(context =>
        {
            context.Response.StatusCode = StatusCodes.Status202Accepted;
            return Task.CompletedTask;
        });
        var dbPath = NewTempSqlitePath();
        var policyPath = WriteTempPolicy(CreatePolicy("audit", "warn", 4096, upstream.BaseAddress, dbPath));
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", policyPath);

        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();

            using var response = await client.PostAsync(
                "/github/mcp",
                new StringContent(ToolsListRequest, Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", null);
        }

        var row = Assert.Single(ReadAuditEvents(dbPath), r => r.EventType == "tools_list_response_inspection");
        Assert.Equal("allow", row.Decision);
        Assert.Equal(string.Empty, row.Reasons);

        using var payload = JsonDocument.Parse(row.PayloadJson);
        var summary = payload.RootElement.GetProperty("argumentSummary");
        Assert.True(summary.GetProperty("inspectionSkipped").GetBoolean());
        Assert.Equal("empty_response", summary.GetProperty("inspectionSkipReason").GetString());

        DeleteIfExists(dbPath);
    }

    [Fact]
    public async Task OversizedEventStreamToolsListResponseBlocksWhenMaxBodyBytesExceeded()
    {
        var responseBody = "data: {\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"tools\":[{\"name\":\""
            + new string('x', 2048)
            + "\"}]}}\n\n";
        await using var upstream = await StartUpstreamAsync(ctx => WriteResponseAsync(ctx, responseBody, "text/event-stream", setLength: false));
        var dbPath = NewTempSqlitePath();
        var policyPath = WriteTempPolicy(CreatePolicy("enforce", "block", 512, upstream.BaseAddress, dbPath));
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", policyPath);

        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();

            using var response = await client.PostAsync(
                "/github/mcp",
                new StringContent(ToolsListRequest, Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.Contains("inspection_unsupported", body, StringComparison.Ordinal);
            Assert.DoesNotContain(responseBody, body, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", null);
        }

        var row = Assert.Single(ReadAuditEvents(dbPath), r => r.EventType == "tools_list_response_inspection");
        Assert.Equal("block", row.Decision);
        Assert.Contains("inspection_unsupported", row.Reasons, StringComparison.Ordinal);

        DeleteIfExists(dbPath);
    }

    [Fact]
    public async Task OversizedToolsListResponseBlocksWithoutReturningUpstreamBody()
    {
        var responseBody = "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"tools\":[{\"name\":\""
            + new string('x', 2048)
            + "\"}]}}";
        await using var upstream = await StartUpstreamAsync(ctx => WriteResponseAsync(ctx, responseBody, "application/json"));
        var dbPath = NewTempSqlitePath();
        var policyPath = WriteTempPolicy(CreatePolicy("enforce", "block", 128, upstream.BaseAddress, dbPath));
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", policyPath);

        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();

            using var response = await client.PostAsync(
                "/github/mcp",
                new StringContent(ToolsListRequest, Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.Contains("inspection_unsupported", body, StringComparison.Ordinal);
            Assert.DoesNotContain(new string('x', 128), body, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", null);
        }

        var row = Assert.Single(ReadAuditEvents(dbPath), r => r.EventType == "tools_list_response_inspection");
        Assert.Equal("block", row.Decision);
        Assert.Contains("inspection_unsupported", row.Reasons, StringComparison.Ordinal);

        DeleteIfExists(dbPath);
    }

    [Fact]
    public async Task AuditModeDescriptionDriftWarnsAndReturnsOriginalResponse()
    {
        const string responseBody = """
            {"jsonrpc":"2.0","id":1,"result":{"tools":[{"name":"repos.search","title":"Search","description":"New description","inputSchema":{"type":"object"}}]}}
            """;
        await using var upstream = await StartUpstreamAsync(ctx => WriteResponseAsync(ctx, responseBody, "application/json"));
        var dbPath = NewTempSqlitePath();
        await SeedSchemaVersionAsync(
            dbPath,
            "github",
            $"{upstream.BaseAddress}/mcp",
            new DiscoveredTool("repos.search", "Search", "Old description", JsonNode.Parse("""{"type":"object"}"""), null));
        var policyPath = WriteTempPolicy(CreatePolicy("audit", "warn", 4096, upstream.BaseAddress, dbPath));
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", policyPath);

        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();

            using var response = await client.PostAsync(
                "/github/mcp",
                new StringContent(ToolsListRequest, Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(responseBody, await response.Content.ReadAsStringAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", null);
        }

        var row = Assert.Single(ReadAuditEvents(dbPath), r => r.EventType == "tools_list_response_inspection");
        Assert.Equal("warn", row.Decision);
        Assert.Contains("tool_description_changed", row.Reasons, StringComparison.Ordinal);

        using var payload = JsonDocument.Parse(row.PayloadJson);
        var driftedToolNames = payload.RootElement
            .GetProperty("argumentSummary")
            .GetProperty("driftedToolNames")
            .EnumerateArray()
            .Select(name => name.GetString()!)
            .ToArray();
        Assert.Equal(["repos.search"], driftedToolNames);

        var reviews = ReadDriftReviews(dbPath);
        var review = Assert.Single(reviews);
        Assert.Equal("github", review.ServerId);
        Assert.Equal("repos.search", review.ToolName);
        Assert.Equal("description", review.FieldName);
        Assert.Equal(1, review.FromVersion);
        Assert.Equal(2, review.ToVersion);
        Assert.Equal("pending", review.Status);
        Assert.Equal("tool_description_changed", review.Reasons);
        Assert.StartsWith("sha256:", review.PolicyVersion, StringComparison.Ordinal);

        var driftReviewIds = payload.RootElement
            .GetProperty("argumentSummary")
            .GetProperty("driftReviewIds")
            .EnumerateArray()
            .Select(id => id.GetInt64())
            .ToArray();
        Assert.Equal([review.Id], driftReviewIds);

        var diffMetadata = Assert.Single(ReadDiffMetadata(dbPath));
        Assert.Equal(review.Id, diffMetadata.DriftReviewId);
        Assert.Contains("Old description", diffMetadata.BeforeJson, StringComparison.Ordinal);
        Assert.Contains("New description", diffMetadata.AfterJson, StringComparison.Ordinal);
        Assert.StartsWith("sha256:", diffMetadata.BeforeHash, StringComparison.Ordinal);
        Assert.StartsWith("sha256:", diffMetadata.AfterHash, StringComparison.Ordinal);

        Assert.Equal(2, await CountSchemaVersionsAsync(dbPath, "github"));

        DeleteIfExists(dbPath);
    }

    [Fact]
    public async Task EnforceModeSchemaDriftBlocksWithoutReturningUpstreamToolSurface()
    {
        const string responseBody = """
            {"jsonrpc":"2.0","id":1,"result":{"tools":[{"name":"repos.search","title":"Search","description":"Description","inputSchema":{"type":"object","properties":{"q":{"type":"string"},"limit":{"type":"integer"}}}}]}}
            """;
        await using var upstream = await StartUpstreamAsync(ctx => WriteResponseAsync(ctx, responseBody, "application/json"));
        var dbPath = NewTempSqlitePath();
        await SeedSchemaVersionAsync(
            dbPath,
            "github",
            $"{upstream.BaseAddress}/mcp",
            new DiscoveredTool(
                "repos.search",
                "Search",
                "Description",
                JsonNode.Parse("""{"type":"object","properties":{"q":{"type":"string"}}}"""),
                null));
        var policyPath = WriteTempPolicy(CreatePolicy("enforce", "warn", 4096, upstream.BaseAddress, dbPath));
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", policyPath);

        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();

            using var response = await client.PostAsync(
                "/github/mcp",
                new StringContent(ToolsListRequest, Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.Contains("tool_schema_changed", body, StringComparison.Ordinal);
            Assert.DoesNotContain("limit", body, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", null);
        }

        var row = Assert.Single(ReadAuditEvents(dbPath), r => r.EventType == "tools_list_response_inspection");
        Assert.Equal("block", row.Decision);
        Assert.Contains("tool_schema_changed", row.Reasons, StringComparison.Ordinal);

        var reviews = ReadDriftReviews(dbPath);
        var review = Assert.Single(reviews);
        Assert.Equal("github", review.ServerId);
        Assert.Equal("repos.search", review.ToolName);
        Assert.Equal("schema", review.FieldName);
        Assert.Equal(1, review.FromVersion);
        Assert.Equal(2, review.ToVersion);
        Assert.Equal("pending", review.Status);
        Assert.Equal("tool_schema_changed", review.Reasons);

        using var payload = JsonDocument.Parse(row.PayloadJson);
        var driftReviewIds = payload.RootElement
            .GetProperty("argumentSummary")
            .GetProperty("driftReviewIds")
            .EnumerateArray()
            .Select(id => id.GetInt64())
            .ToArray();
        Assert.Equal([review.Id], driftReviewIds);

        var diffMetadata = Assert.Single(ReadDiffMetadata(dbPath));
        Assert.Equal(review.Id, diffMetadata.DriftReviewId);
        Assert.Contains("\"q\"", diffMetadata.BeforeJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"limit\"", diffMetadata.BeforeJson, StringComparison.Ordinal);
        Assert.Contains("\"limit\"", diffMetadata.AfterJson, StringComparison.Ordinal);
        Assert.StartsWith("sha256:", diffMetadata.BeforeHash, StringComparison.Ordinal);
        Assert.StartsWith("sha256:", diffMetadata.AfterHash, StringComparison.Ordinal);

        Assert.Equal(2, await CountSchemaVersionsAsync(dbPath, "github"));

        DeleteIfExists(dbPath);
    }

    [Fact]
    public async Task ApprovedCurrentDriftReviewAllowsObservedSurfaceInEnforceMode()
    {
        const string responseBody = """
            {"jsonrpc":"2.0","id":1,"result":{"tools":[{"name":"repos.search","title":"Search","description":"Description","inputSchema":{"type":"object","properties":{"q":{"type":"string"},"limit":{"type":"integer"}}}}]}}
            """;
        await using var upstream = await StartUpstreamAsync(ctx => WriteResponseAsync(ctx, responseBody, "application/json"));
        var dbPath = NewTempSqlitePath();
        await SeedReviewedCurrentSchemaAsync(dbPath, "github", $"{upstream.BaseAddress}/mcp", status: "approved");
        var policyPath = WriteTempPolicy(CreatePolicy("enforce", "warn", 4096, upstream.BaseAddress, dbPath));
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", policyPath);

        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();

            using var response = await client.PostAsync(
                "/github/mcp",
                new StringContent(ToolsListRequest, Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(responseBody, await response.Content.ReadAsStringAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", null);
        }

        var row = Assert.Single(ReadAuditEvents(dbPath), r => r.EventType == "tools_list_response_inspection");
        Assert.Equal("allow", row.Decision);
        Assert.DoesNotContain("driftReviewIds", row.PayloadJson, StringComparison.Ordinal);

        DeleteIfExists(dbPath);
    }

    [Theory]
    [InlineData("rejected")]
    [InlineData("blocked")]
    public async Task UnapprovedCurrentDriftReviewBlocksObservedSurfaceInEnforceMode(string reviewStatus)
    {
        const string responseBody = """
            {"jsonrpc":"2.0","id":1,"result":{"tools":[{"name":"repos.search","title":"Search","description":"Description","inputSchema":{"type":"object","properties":{"q":{"type":"string"},"limit":{"type":"integer"}}}}]}}
            """;
        await using var upstream = await StartUpstreamAsync(ctx => WriteResponseAsync(ctx, responseBody, "application/json"));
        var dbPath = NewTempSqlitePath();
        var review = await SeedReviewedCurrentSchemaAsync(dbPath, "github", $"{upstream.BaseAddress}/mcp", reviewStatus);
        var policyPath = WriteTempPolicy(CreatePolicy("enforce", "warn", 4096, upstream.BaseAddress, dbPath));
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", policyPath);

        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();

            using var response = await client.PostAsync(
                "/github/mcp",
                new StringContent(ToolsListRequest, Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.Contains("tool_schema_changed", body, StringComparison.Ordinal);
            Assert.DoesNotContain("limit", body, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", null);
        }

        var row = Assert.Single(ReadAuditEvents(dbPath), r => r.EventType == "tools_list_response_inspection");
        Assert.Equal("block", row.Decision);
        Assert.Contains("tool_schema_changed", row.Reasons, StringComparison.Ordinal);

        using var payload = JsonDocument.Parse(row.PayloadJson);
        var driftReviewIds = payload.RootElement
            .GetProperty("argumentSummary")
            .GetProperty("driftReviewIds")
            .EnumerateArray()
            .Select(id => id.GetInt64())
            .ToArray();
        Assert.Equal([review.Row.Id], driftReviewIds);

        DeleteIfExists(dbPath);
    }

    [Fact]
    public async Task SchemaDriftWithValueCaptureDisabledStoresHashOnlyDiffMetadata()
    {
        const string responseBody = """
            {"jsonrpc":"2.0","id":1,"result":{"tools":[{"name":"repos.search","title":"Search","description":"Description","inputSchema":{"type":"object","properties":{"q":{"type":"string"},"limit":{"type":"integer"}}}}]}}
            """;
        await using var upstream = await StartUpstreamAsync(ctx => WriteResponseAsync(ctx, responseBody, "application/json"));
        var dbPath = NewTempSqlitePath();
        await SeedSchemaVersionAsync(
            dbPath,
            "github",
            $"{upstream.BaseAddress}/mcp",
            new DiscoveredTool(
                "repos.search",
                "Search",
                "Description",
                JsonNode.Parse("""{"type":"object","properties":{"q":{"type":"string"}}}"""),
                null),
            captureMetadata: false);
        var policyPath = WriteTempPolicy(CreatePolicy("audit", "warn", 4096, upstream.BaseAddress, dbPath));
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", policyPath);
        Environment.SetEnvironmentVariable("PROXYWARD_SCHEMA_DIFF_CAPTURE_VALUES", "false");

        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();

            using var response = await client.PostAsync(
                "/github/mcp",
                new StringContent(ToolsListRequest, Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(responseBody, await response.Content.ReadAsStringAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", null);
            Environment.SetEnvironmentVariable("PROXYWARD_SCHEMA_DIFF_CAPTURE_VALUES", null);
        }

        var review = Assert.Single(ReadDriftReviews(dbPath));
        var diffMetadata = Assert.Single(ReadDiffMetadata(dbPath));
        Assert.Equal(review.Id, diffMetadata.DriftReviewId);
        Assert.Null(diffMetadata.BeforeJson);
        Assert.Null(diffMetadata.AfterJson);
        Assert.StartsWith("sha256:", diffMetadata.BeforeHash, StringComparison.Ordinal);
        Assert.StartsWith("sha256:", diffMetadata.AfterHash, StringComparison.Ordinal);

        DeleteIfExists(dbPath);
    }

    [Fact]
    public async Task RepeatedDescriptionDriftDoesNotCreateDuplicatePendingReviewRows()
    {
        const string responseBody = """
            {"jsonrpc":"2.0","id":1,"result":{"tools":[{"name":"repos.search","title":"Search","description":"New description","inputSchema":{"type":"object"}}]}}
            """;
        await using var upstream = await StartUpstreamAsync(ctx => WriteResponseAsync(ctx, responseBody, "application/json"));
        var dbPath = NewTempSqlitePath();
        await SeedSchemaVersionAsync(
            dbPath,
            "github",
            $"{upstream.BaseAddress}/mcp",
            new DiscoveredTool("repos.search", "Search", "Old description", JsonNode.Parse("""{"type":"object"}"""), null));
        var policyPath = WriteTempPolicy(CreatePolicy("audit", "warn", 4096, upstream.BaseAddress, dbPath));
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", policyPath);

        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();

            using var first = await client.PostAsync(
                "/github/mcp",
                new StringContent(ToolsListRequest, Encoding.UTF8, "application/json"));
            using var second = await client.PostAsync(
                "/github/mcp",
                new StringContent(ToolsListRequest, Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", null);
        }

        var review = Assert.Single(ReadDriftReviews(dbPath));
        Assert.Equal("pending", review.Status);
        Assert.Equal(1, review.FromVersion);
        Assert.Equal(2, review.ToVersion);

        DeleteIfExists(dbPath);
    }

    [Fact]
    public async Task DriftReviewWriteFailurePreservesAuditModeResponseAndOmitsReviewIds()
    {
        const string responseBody = """
            {"jsonrpc":"2.0","id":1,"result":{"tools":[{"name":"repos.search","title":"Search","description":"New description","inputSchema":{"type":"object"}}]}}
            """;
        await using var upstream = await StartUpstreamAsync(ctx => WriteResponseAsync(ctx, responseBody, "application/json"));
        var dbPath = NewTempSqlitePath();
        await SeedSchemaVersionAsync(
            dbPath,
            "github",
            $"{upstream.BaseAddress}/mcp",
            new DiscoveredTool("repos.search", "Search", "Old description", JsonNode.Parse("""{"type":"object"}"""), null));
        var policyPath = WriteTempPolicy(CreatePolicy("audit", "warn", 4096, upstream.BaseAddress, dbPath));
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", policyPath);

        try
        {
            await using var factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder =>
                {
                    builder.ConfigureTestServices(services =>
                    {
                        services.RemoveAll<ISchemaDriftReviewStore>();
                        services.AddSingleton<ISchemaDriftReviewStore>(
                            new ThrowingDriftReviewStore(SchemaLockWriteFailureReasons.DbLocked));
                    });
                });
            using var client = factory.CreateClient();

            using var response = await client.PostAsync(
                "/github/mcp",
                new StringContent(ToolsListRequest, Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(responseBody, await response.Content.ReadAsStringAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", null);
        }

        var row = Assert.Single(ReadAuditEvents(dbPath), r => r.EventType == "tools_list_response_inspection");
        Assert.Equal("warn", row.Decision);
        Assert.Contains("tool_description_changed", row.Reasons, StringComparison.Ordinal);

        using var payload = JsonDocument.Parse(row.PayloadJson);
        Assert.False(payload.RootElement
            .GetProperty("argumentSummary")
            .TryGetProperty("driftReviewIds", out _));

        DeleteIfExists(dbPath);
    }

    [Fact]
    public async Task FirstObservationPersistsVersionOneWithNoDrift()
    {
        const string responseBody = """
            {"jsonrpc":"2.0","id":1,"result":{"tools":[{"name":"repos.search","title":"Search","description":"Description","inputSchema":{"type":"object"}}]}}
            """;
        await using var upstream = await StartUpstreamAsync(ctx => WriteResponseAsync(ctx, responseBody, "application/json"));
        var dbPath = NewTempSqlitePath();
        var policyPath = WriteTempPolicy(CreatePolicy("enforce", "warn", 4096, upstream.BaseAddress, dbPath));
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", policyPath);

        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();

            using var response = await client.PostAsync(
                "/github/mcp",
                new StringContent(ToolsListRequest, Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(responseBody, await response.Content.ReadAsStringAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", null);
        }

        var row = Assert.Single(ReadAuditEvents(dbPath), r => r.EventType == "tools_list_response_inspection");
        Assert.Equal("allow", row.Decision);
        Assert.Equal(string.Empty, row.Reasons);

        Assert.Equal(1, await CountSchemaVersionsAsync(dbPath, "github"));

        DeleteIfExists(dbPath);
    }

    [Theory]
    [InlineData("audit")]
    [InlineData("enforce")]
    public async Task SchemaLockWriteFailureReturnsUpstreamPayloadAndEmitsMetric(string mode)
    {
        using var metrics = new MetricCollector();
        const string responseBody = """
            {"jsonrpc":"2.0","id":1,"result":{"tools":[{"name":"repos.search","title":"Search","description":"Description","inputSchema":{"type":"object"}}]}}
            """;
        await using var upstream = await StartUpstreamAsync(ctx => WriteResponseAsync(ctx, responseBody, "application/json"));
        var dbPath = NewTempSqlitePath();
        var policyPath = WriteTempPolicy(CreatePolicy(mode, "warn", 4096, upstream.BaseAddress, dbPath));
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", policyPath);

        try
        {
            await using var factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder =>
                {
                    builder.ConfigureTestServices(services =>
                    {
                        services.RemoveAll<ITrackedToolSchemaStore>();
                        services.AddSingleton<ITrackedToolSchemaStore>(
                            new ThrowingSchemaStore(SchemaLockWriteFailureReasons.DbLocked));
                    });
                });
            using var client = factory.CreateClient();

            using var response = await client.PostAsync(
                "/github/mcp",
                new StringContent(ToolsListRequest, Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(responseBody, await response.Content.ReadAsStringAsync());
            Assert.Equal(1, upstream.RequestCount);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", null);
        }

        Assert.Contains(metrics.Snapshots, measurement => measurement.Name == ProxyWardTelemetry.SchemaWriteFailedMetric
            && measurement.Tags.TryGetValue(ProxyWardTelemetry.SchemaServerIdTag, out var serverId)
            && serverId == "github"
            && measurement.Tags.TryGetValue(ProxyWardTelemetry.SchemaFailureReasonTag, out var reason)
            && reason == SchemaLockWriteFailureReasons.DbLocked);

        DeleteIfExists(dbPath);
    }

    [Fact]
    public async Task RenamedServerStartsNewVersionChainAndLeavesPriorChainUntouched()
    {
        const string responseBody = """
            {"jsonrpc":"2.0","id":1,"result":{"tools":[{"name":"repos.search","title":"Search","description":"Description","inputSchema":{"type":"object"}}]}}
            """;
        await using var upstream = await StartUpstreamAsync(ctx => WriteResponseAsync(ctx, responseBody, "application/json"));
        var dbPath = NewTempSqlitePath();

        await RunToolsListOnceAsync(
            CreateSingleServerPolicy("alpha", "/alpha/mcp", upstream.BaseAddress, dbPath),
            "/alpha/mcp");
        await RunToolsListOnceAsync(
            CreateSingleServerPolicy("beta", "/beta/mcp", upstream.BaseAddress, dbPath),
            "/beta/mcp");

        Assert.Equal(1, await CountSchemaVersionsAsync(dbPath, "alpha"));
        Assert.Equal(1, await CountSchemaVersionsAsync(dbPath, "beta"));
        Assert.Equal(1, await LatestSchemaVersionAsync(dbPath, "alpha"));
        Assert.Equal(1, await LatestSchemaVersionAsync(dbPath, "beta"));

        DeleteIfExists(dbPath);
    }

    [Fact]
    public async Task RemovedServerLeavesPriorSchemaRowsIntactOnNextStartup()
    {
        const string responseBody = """
            {"jsonrpc":"2.0","id":1,"result":{"tools":[{"name":"repos.search","title":"Search","description":"Description","inputSchema":{"type":"object"}}]}}
            """;
        await using var upstream = await StartUpstreamAsync(ctx => WriteResponseAsync(ctx, responseBody, "application/json"));
        var dbPath = NewTempSqlitePath();

        await RunToolsListOnceAsync(
            CreateSingleServerPolicy("alpha", "/alpha/mcp", upstream.BaseAddress, dbPath),
            "/alpha/mcp");

        var before = await CountSchemaVersionsAsync(dbPath, "alpha");
        var policyPath = WriteTempPolicy(CreateSingleServerPolicy("beta", "/beta/mcp", upstream.BaseAddress, dbPath));
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", policyPath);

        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();
            using var response = await client.GetAsync("/health");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", null);
        }

        Assert.Equal(1, before);
        Assert.Equal(1, await CountSchemaVersionsAsync(dbPath, "alpha"));
        Assert.Equal(0, await CountSchemaVersionsAsync(dbPath, "beta"));

        DeleteIfExists(dbPath);
    }

    private const string ToolsListRequest = """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""";

    private static async Task WriteResponseAsync(
        HttpContext context,
        string body,
        string contentType,
        bool setLength = true)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Response.ContentType = contentType;
        if (setLength)
        {
            context.Response.ContentLength = bytes.Length;
        }

        await context.Response.Body.WriteAsync(bytes);
    }

    private static async Task WriteCompressedResponseAsync(
        HttpContext context,
        string body,
        string contentType,
        string contentEncoding)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        await using var compressed = new MemoryStream();
        await using (var gzip = new GZipStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            await gzip.WriteAsync(bytes);
        }

        var compressedBytes = compressed.ToArray();
        context.Response.ContentType = contentType;
        context.Response.Headers["Content-Encoding"] = contentEncoding;
        context.Response.ContentLength = compressedBytes.Length;
        await context.Response.Body.WriteAsync(compressedBytes);
    }

    private static async Task<UpstreamApp> StartUpstreamAsync(Func<HttpContext, Task> handler)
    {
        var port = GetFreePort();
        var baseAddress = $"http://127.0.0.1:{port}";
        var counter = new RequestCounter();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(baseAddress);

        var app = builder.Build();
        app.Map("/{**path}", async context =>
        {
            counter.Increment();
            await handler(context);
        });

        await app.StartAsync();
        return new UpstreamApp(baseAddress, app, counter);
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string WriteTempPolicy(string yaml)
    {
        var path = Path.Combine(Path.GetTempPath(), $"proxyward-{Guid.NewGuid():N}.yaml");
        new ProxyWard.Policy.Persistence.SqlitePolicyStore(path).SaveAsync(yaml).GetAwaiter().GetResult();
        return path;
    }

    private static string NewTempSqlitePath() =>
        Path.Combine(Path.GetTempPath(), $"proxyward-audit-{Guid.NewGuid():N}.db");

    private static async Task SeedSchemaVersionAsync(
        string dbPath,
        string serverId,
        string upstreamUrl,
        DiscoveredTool tool,
        bool captureMetadata = true)
    {
        using var store = new SqliteTrackedToolSchemaStore(dbPath);
        var fingerprinter = new ToolFingerprinter();
        var options = new ToolSchemaDiffMetadataOptions(
            CaptureValues: captureMetadata,
            MaxValueBytes: ToolSchemaDiffMetadataOptions.Default.MaxValueBytes);
        var snapshot = new ToolSchemaSnapshotInput(
            serverId,
            upstreamUrl,
            "2025-11-25",
            [SafeToolSchemaMetadata.CreateSnapshotEntry(tool, fingerprinter, options)]);

        await store.RecordAsync(snapshot, new DateTimeOffset(2026, 5, 3, 12, 0, 0, TimeSpan.Zero), CancellationToken.None);
    }

    private static async Task<DriftReviewRecordResult> SeedReviewedCurrentSchemaAsync(
        string dbPath,
        string serverId,
        string upstreamUrl,
        string status)
    {
        var oldTool = new DiscoveredTool(
            "repos.search",
            "Search",
            "Description",
            JsonNode.Parse("""{"type":"object","properties":{"q":{"type":"string"}}}"""),
            null);
        var currentTool = new DiscoveredTool(
            "repos.search",
            "Search",
            "Description",
            JsonNode.Parse("""{"type":"object","properties":{"q":{"type":"string"},"limit":{"type":"integer"}}}"""),
            null);

        await SeedSchemaVersionAsync(dbPath, serverId, upstreamUrl, oldTool);
        await SeedSchemaVersionAsync(dbPath, serverId, upstreamUrl, currentTool);

        using var reviewStore = new SqliteSchemaDriftReviewStore(dbPath);
        var result = await reviewStore.RecordObservationAsync(
            new DriftReviewObservation(
                ServerId: serverId,
                ToolName: "repos.search",
                FieldName: "schema",
                FromVersion: 1,
                ToVersion: 2,
                Reasons: [PolicyReasonCodes.ToolSchemaChanged],
                PolicyVersion: "sha256:policy",
                DetectedAtUtc: new DateTimeOffset(2026, 5, 10, 10, 0, 0, TimeSpan.Zero)),
            CancellationToken.None);

        await UpdateReviewStatusAsync(dbPath, result.Row.Id, status);
        return result;
    }

    private static async Task UpdateReviewStatusAsync(string dbPath, long reviewId, string status)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString());
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE schema_drift_reviews
            SET status = $status,
                reviewed_at_utc = $reviewed_at_utc,
                reviewed_by = $reviewed_by
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$reviewed_at_utc", "2026-05-10T10:30:00.0000000Z");
        command.Parameters.AddWithValue("$reviewed_by", "test");
        command.Parameters.AddWithValue("$id", reviewId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountSchemaVersionsAsync(string dbPath, string serverId)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString());

        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM tool_schema_versions WHERE server_id = $server_id;";
        command.Parameters.AddWithValue("$server_id", serverId);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<int> LatestSchemaVersionAsync(string dbPath, string serverId)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString());

        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT version FROM tool_schema_versions WHERE server_id = $server_id ORDER BY version DESC LIMIT 1;";
        command.Parameters.AddWithValue("$server_id", serverId);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task RunToolsListOnceAsync(string policyYaml, string route)
    {
        var policyPath = WriteTempPolicy(policyYaml);
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", policyPath);

        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();

            using var response = await client.PostAsync(
                route,
                new StringContent(ToolsListRequest, Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", null);
        }
    }

    private static void DeleteIfExists(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup; SQLite shared cache may delay file release on Windows.
        }
    }

    private static string CreatePolicy(
        string mode,
        string unsupportedStreaming,
        int maxBodyBytes,
        string upstreamBaseAddress,
        string sqlitePath) =>
        $$"""
        mode: {{mode}}
        inspection:
          maxBodyBytes: {{maxBodyBytes}}
          unsupportedStreaming: {{unsupportedStreaming}}
          batchToolCalls: failClosed
        audit:
          sink: sqlite
          sqlitePath: {{sqlitePath.Replace("\\", "/")}}
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
          github:
            route: /github/mcp
            upstream: {{upstreamBaseAddress}}/mcp
            allowed: true
            tools:
              default: deny
              allow: []
              block: []
            arguments:
              paths:
                allowedRoots:
                  - /workspace
                blockTraversal: true
              hosts:
                allow: []
                blockPrivateNetworks: true
              commands:
                blockShell: true
                dangerous:
                  - rm
        """;

    private static string CreateSingleServerPolicy(
        string serverId,
        string route,
        string upstreamBaseAddress,
        string sqlitePath) =>
        $$"""
        mode: audit
        inspection:
          maxBodyBytes: 4096
          unsupportedStreaming: warn
          batchToolCalls: failClosed
        audit:
          sink: sqlite
          sqlitePath: {{sqlitePath.Replace("\\", "/")}}
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
          {{serverId}}:
            route: {{route}}
            upstream: {{upstreamBaseAddress}}/mcp
            allowed: true
            tools:
              default: deny
              allow: []
              block: []
            arguments:
              paths:
                allowedRoots:
                  - /workspace
                blockTraversal: true
              hosts:
                allow: []
                blockPrivateNetworks: true
              commands:
                blockShell: true
                dangerous:
                  - rm
        """;

    private static List<DriftReviewDbRow> ReadDriftReviews(string path) =>
        AuditDatabase.ReadEventually(() =>
        {
            var rows = new List<DriftReviewDbRow>();
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly
            }.ToString());

            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, server_id, tool_name, field_name, from_version, to_version,
                       status, reasons, policy_version
                FROM schema_drift_reviews
                ORDER BY id ASC;
                """;

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new DriftReviewDbRow(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetInt32(4),
                    reader.GetInt32(5),
                    reader.GetString(6),
                    reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8)));
            }

            return rows;
        });

    private static List<DiffMetadataDbRow> ReadDiffMetadata(string path) =>
        AuditDatabase.ReadEventually(() =>
        {
            var rows = new List<DiffMetadataDbRow>();
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly
            }.ToString());

            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, drift_review_id, before_json, after_json, before_hash, after_hash
                FROM tool_schema_diff_metadata
                ORDER BY id ASC;
                """;

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new DiffMetadataDbRow(
                    reader.GetInt64(0),
                    reader.GetInt64(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5)));
            }

            return rows;
        });

    private static List<AuditRow> ReadAuditEvents(string path) =>
        AuditDatabase.ReadEventually(() =>
        {
            var rows = new List<AuditRow>();
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly
            }.ToString());

            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT event_type, mode, decision, server_id, method, tool_name,
                       reasons, payload_json
                FROM audit_events
                ORDER BY id ASC;
                """;

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new AuditRow(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.GetString(6),
                    reader.GetString(7)));
            }

            return rows;
        });

    private sealed record AuditRow(
        string EventType,
        string Mode,
        string Decision,
        string ServerId,
        string? Method,
        string? ToolName,
        string Reasons,
        string PayloadJson);

    private sealed record DriftReviewDbRow(
        long Id,
        string ServerId,
        string ToolName,
        string FieldName,
        int FromVersion,
        int ToVersion,
        string Status,
        string Reasons,
        string? PolicyVersion);

    private sealed record DiffMetadataDbRow(
        long Id,
        long DriftReviewId,
        string? BeforeJson,
        string? AfterJson,
        string BeforeHash,
        string AfterHash);

    private sealed class ThrowingSchemaStore(string reason) : ITrackedToolSchemaStore
    {
        public ValueTask<ToolSchemaVersionRow?> GetLatestAsync(
            string serverId,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<ToolSchemaVersionRow?>(null);

        public ValueTask<RecordedVersion> RecordAsync(
            ToolSchemaSnapshotInput snapshot,
            DateTimeOffset capturedAtUtc,
            CancellationToken cancellationToken) =>
            throw new SchemaLockWriteFailedException(
                reason,
                "Simulated schema-lock write failure.",
                new IOException("simulated"));
    }

    private sealed class ThrowingDriftReviewStore(string reason) : ISchemaDriftReviewStore
    {
        public ValueTask<DriftReviewRecordResult> RecordObservationAsync(
            DriftReviewObservation observation,
            CancellationToken cancellationToken) =>
            throw new SchemaLockWriteFailedException(
                reason,
                "Simulated drift-review write failure.",
                new IOException("simulated"));

        public ValueTask<IReadOnlyList<DriftReviewRow>> GetByServerAsync(
            string serverId,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<DriftReviewRow>>([]);
    }

    private sealed record MeasurementSnapshot(
        string Name,
        long Value,
        IReadOnlyDictionary<string, string?> Tags);

    private sealed class MetricCollector : IDisposable
    {
        private readonly ConcurrentQueue<MeasurementSnapshot> _snapshots = new();
        private readonly MeterListener _listener = new();

        public MetricCollector()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == ProxyWardTelemetry.MeterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };

            _listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
            {
                var copiedTags = new Dictionary<string, string?>(StringComparer.Ordinal);
                foreach (var tag in tags)
                {
                    copiedTags[tag.Key] = tag.Value?.ToString();
                }

                _snapshots.Enqueue(new MeasurementSnapshot(instrument.Name, measurement, copiedTags));
            });
            _listener.Start();
        }

        public IReadOnlyCollection<MeasurementSnapshot> Snapshots => _snapshots.ToArray();

        public void Dispose() => _listener.Dispose();
    }

    private sealed class RequestCounter
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);
        public void Increment() => Interlocked.Increment(ref _count);
    }

    private sealed class UpstreamApp(string baseAddress, WebApplication app, RequestCounter counter) : IAsyncDisposable
    {
        public string BaseAddress { get; } = baseAddress;
        public int RequestCount => counter.Count;

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
