using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;

namespace ProxyWard.IntegrationTests;

public class ProxyControlEndpointTests
{
    [Fact]
    public async Task ControlStatusIsNotAvailableWhenRuntimeControlIsDisabled()
    {
        var policyPath = TestFiles.SavePolicy(ValidYaml);
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", policyPath);
        Environment.SetEnvironmentVariable("PROXYWARD_CONTROL_ENABLED", null);
        Environment.SetEnvironmentVariable("PROXYWARD_CONTROL_TOKEN", null);

        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/control/status");

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        finally
        {
            ClearProxyWardEnvironment();
        }
    }

    [Fact]
    public async Task ControlStatusRequiresBearerTokenWhenRuntimeControlIsEnabled()
    {
        var policyPath = TestFiles.SavePolicy(ValidYaml);
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", policyPath);
        Environment.SetEnvironmentVariable("PROXYWARD_CONTROL_ENABLED", "true");
        Environment.SetEnvironmentVariable("PROXYWARD_CONTROL_TOKEN", "test-control-token");

        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/control/status");

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        finally
        {
            ClearProxyWardEnvironment();
        }
    }

    [Fact]
    public async Task ControlStatusRejectsInvalidBearerTokenWhenRuntimeControlIsEnabled()
    {
        var policyPath = TestFiles.SavePolicy(ValidYaml);
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", policyPath);
        Environment.SetEnvironmentVariable("PROXYWARD_CONTROL_ENABLED", "true");
        Environment.SetEnvironmentVariable("PROXYWARD_CONTROL_TOKEN", "test-control-token");

        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "wrong-token");

            using var response = await client.GetAsync("/control/status");

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        finally
        {
            ClearProxyWardEnvironment();
        }
    }

    [Fact]
    public async Task ControlAuthFailureIsAuditedWithoutTokenValues()
    {
        const string expectedToken = "test-control-token";
        const string suppliedToken = "wrong-token";
        var databasePath = Path.Combine(Path.GetTempPath(), $"proxyward-control-auth-{Guid.NewGuid():N}.db");
        var policyPath = TestFiles.SavePolicy(ValidYaml.Replace(
            "sqlitePath: ./data/proxyward.db",
            $"sqlitePath: {TestFiles.YamlPath(databasePath)}",
            StringComparison.Ordinal));
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", policyPath);
        Environment.SetEnvironmentVariable("PROXYWARD_CONTROL_ENABLED", "true");
        Environment.SetEnvironmentVariable("PROXYWARD_CONTROL_TOKEN", expectedToken);

        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", suppliedToken);

            using var response = await client.GetAsync("/control/status");

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

            var audit = Assert.Single(await ReadControlAuthFailureAuditRowsAsync(databasePath));
            Assert.Equal("bearer_token_invalid", audit.Reasons);
            Assert.True(audit.DurationMs >= 0);
            Assert.DoesNotContain(expectedToken, audit.PayloadJson, StringComparison.Ordinal);
            Assert.DoesNotContain(suppliedToken, audit.PayloadJson, StringComparison.Ordinal);
            Assert.DoesNotContain(expectedToken, audit.Reasons, StringComparison.Ordinal);
            Assert.DoesNotContain(suppliedToken, audit.Reasons, StringComparison.Ordinal);
            using (var auditPayload = JsonDocument.Parse(audit.PayloadJson))
            {
                Assert.Equal(audit.DurationMs, auditPayload.RootElement.GetProperty("durationMs").GetInt64());
            }
        }
        finally
        {
            ClearProxyWardEnvironment();
            TestFiles.DeleteSqlite(databasePath);
        }
    }

    [Fact]
    public async Task ControlStatusReturnsRuntimeMetadataWithValidBearerToken()
    {
        var policyPath = TestFiles.SavePolicy(ValidYaml);
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", policyPath);
        Environment.SetEnvironmentVariable("PROXYWARD_CONTROL_ENABLED", "true");
        Environment.SetEnvironmentVariable("PROXYWARD_CONTROL_TOKEN", "test-control-token");

        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-control-token");

            using var response = await client.GetAsync("/control/status");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var payload = await JsonDocument.ParseAsync(stream);

            Assert.Equal("healthy", payload.RootElement.GetProperty("status").GetString());
            Assert.Equal("MCP ProxyWard", payload.RootElement.GetProperty("service").GetString());
            Assert.Equal("audit", payload.RootElement.GetProperty("mode").GetString());
            Assert.Equal(1, payload.RootElement.GetProperty("serverCount").GetInt32());
            Assert.Equal(1, payload.RootElement.GetProperty("routeVersion").GetInt32());
            Assert.StartsWith("sha256:", payload.RootElement.GetProperty("policyVersion").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            ClearProxyWardEnvironment();
        }
    }

    [Fact]
    public async Task ControlPolicySnapshotAppliesValidSnapshotForNewStatusReads()
    {
        var policyPath = TestFiles.SavePolicy(ValidYaml);
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", policyPath);
        Environment.SetEnvironmentVariable("PROXYWARD_CONTROL_ENABLED", "true");
        Environment.SetEnvironmentVariable("PROXYWARD_CONTROL_TOKEN", "test-control-token");

        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-control-token");

            using var beforeResponse = await client.GetAsync("/control/status");
            using var before = await TestJson.ReadOkAsync(beforeResponse);
            var beforePolicyVersion = before.RootElement.GetProperty("policyVersion").GetString();

            using var applyResponse = await client.PutAsync(
                "/control/policy-snapshot",
                new StringContent(ValidYaml.Replace("mode: audit", "mode: enforce", StringComparison.Ordinal), Encoding.UTF8, "text/yaml"));

            Assert.Equal(HttpStatusCode.OK, applyResponse.StatusCode);

            await using var applyStream = await applyResponse.Content.ReadAsStreamAsync();
            using var applyPayload = await JsonDocument.ParseAsync(applyStream);

            Assert.Equal("enforce", applyPayload.RootElement.GetProperty("mode").GetString());
            Assert.Equal(1, applyPayload.RootElement.GetProperty("serverCount").GetInt32());
            Assert.NotEqual(beforePolicyVersion, applyPayload.RootElement.GetProperty("policyVersion").GetString());

            using var afterResponse = await client.GetAsync("/control/status");
            using var after = await TestJson.ReadOkAsync(afterResponse);

            Assert.Equal("enforce", after.RootElement.GetProperty("mode").GetString());
            Assert.Equal(applyPayload.RootElement.GetProperty("policyVersion").GetString(), after.RootElement.GetProperty("policyVersion").GetString());
        }
        finally
        {
            ClearProxyWardEnvironment();
        }
    }

    [Fact]
    public async Task ControlPolicySnapshotRejectsInvalidYamlAndPreservesActiveSnapshot()
    {
        var policyPath = TestFiles.SavePolicy(ValidYaml);
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", policyPath);
        Environment.SetEnvironmentVariable("PROXYWARD_CONTROL_ENABLED", "true");
        Environment.SetEnvironmentVariable("PROXYWARD_CONTROL_TOKEN", "test-control-token");

        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-control-token");

            using var beforeResponse = await client.GetAsync("/control/status");
            using var before = await TestJson.ReadOkAsync(beforeResponse);
            var beforePolicyVersion = before.RootElement.GetProperty("policyVersion").GetString();

            using var applyResponse = await client.PutAsync(
                "/control/policy-snapshot",
                new StringContent("mode: audit", Encoding.UTF8, "text/yaml"));

            Assert.Equal(HttpStatusCode.BadRequest, applyResponse.StatusCode);

            await using var errorStream = await applyResponse.Content.ReadAsStreamAsync();
            using var errorPayload = await JsonDocument.ParseAsync(errorStream);
            Assert.Equal("policy_validation_failed", errorPayload.RootElement.GetProperty("error").GetString());

            using var afterResponse = await client.GetAsync("/control/status");
            using var after = await TestJson.ReadOkAsync(afterResponse);

            Assert.Equal("audit", after.RootElement.GetProperty("mode").GetString());
            Assert.Equal(beforePolicyVersion, after.RootElement.GetProperty("policyVersion").GetString());
        }
        finally
        {
            ClearProxyWardEnvironment();
        }
    }

    [Fact]
    public async Task ControlPolicySnapshotMakesNewRequestsUseAppliedServerAllowlist()
    {
        var policyPath = TestFiles.SavePolicy(ValidYaml);
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", policyPath);
        Environment.SetEnvironmentVariable("PROXYWARD_CONTROL_ENABLED", "true");
        Environment.SetEnvironmentVariable("PROXYWARD_CONTROL_TOKEN", "test-control-token");

        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-control-token");

            using var applyResponse = await client.PutAsync(
                "/control/policy-snapshot",
                new StringContent(
                    ValidYaml
                        .Replace("mode: audit", "mode: enforce", StringComparison.Ordinal)
                        .Replace("allowed: true", "allowed: false", StringComparison.Ordinal),
                    Encoding.UTF8,
                    "text/yaml"));

            Assert.Equal(HttpStatusCode.OK, applyResponse.StatusCode);

            using var blockedResponse = await client.GetAsync("/sample/mcp");

            Assert.Equal(HttpStatusCode.Forbidden, blockedResponse.StatusCode);

            await using var stream = await blockedResponse.Content.ReadAsStreamAsync();
            using var payload = await JsonDocument.ParseAsync(stream);
            Assert.Equal("block", payload.RootElement.GetProperty("decision").GetString());
            Assert.Equal("sample", payload.RootElement.GetProperty("serverId").GetString());
        }
        finally
        {
            ClearProxyWardEnvironment();
        }
    }

    [Fact]
    public async Task ControlModeAppliesModeOnlySnapshotAndComputesPolicyHash()
    {
        var policyPath = TestFiles.SavePolicy(ValidYaml);
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", policyPath);
        Environment.SetEnvironmentVariable("PROXYWARD_CONTROL_ENABLED", "true");
        Environment.SetEnvironmentVariable("PROXYWARD_CONTROL_TOKEN", "test-control-token");

        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-control-token");

            using var beforeResponse = await client.GetAsync("/control/status");
            using var before = await TestJson.ReadOkAsync(beforeResponse);
            var beforePolicyVersion = before.RootElement.GetProperty("policyVersion").GetString();

            using var applyResponse = await client.PatchAsync(
                "/control/mode",
                new StringContent("""{"mode":"enforce"}""", Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.OK, applyResponse.StatusCode);

            await using var applyStream = await applyResponse.Content.ReadAsStreamAsync();
            using var applyPayload = await JsonDocument.ParseAsync(applyStream);

            Assert.Equal("enforce", applyPayload.RootElement.GetProperty("mode").GetString());
            Assert.Equal(1, applyPayload.RootElement.GetProperty("serverCount").GetInt32());
            Assert.Equal(1, applyPayload.RootElement.GetProperty("routeVersion").GetInt32());
            Assert.NotEqual(beforePolicyVersion, applyPayload.RootElement.GetProperty("policyVersion").GetString());

            using var afterResponse = await client.GetAsync("/control/status");
            using var after = await TestJson.ReadOkAsync(afterResponse);
            Assert.Equal("enforce", after.RootElement.GetProperty("mode").GetString());
            Assert.Equal(applyPayload.RootElement.GetProperty("policyVersion").GetString(), after.RootElement.GetProperty("policyVersion").GetString());
        }
        finally
        {
            ClearProxyWardEnvironment();
        }
    }

    [Fact]
    public async Task ControlModeRejectsInvalidModeAndPreservesActiveSnapshot()
    {
        var policyPath = TestFiles.SavePolicy(ValidYaml);
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", policyPath);
        Environment.SetEnvironmentVariable("PROXYWARD_CONTROL_ENABLED", "true");
        Environment.SetEnvironmentVariable("PROXYWARD_CONTROL_TOKEN", "test-control-token");

        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-control-token");

            using var beforeResponse = await client.GetAsync("/control/status");
            using var before = await TestJson.ReadOkAsync(beforeResponse);
            var beforePolicyVersion = before.RootElement.GetProperty("policyVersion").GetString();

            using var applyResponse = await client.PatchAsync(
                "/control/mode",
                new StringContent("""{"mode":"observe"}""", Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.BadRequest, applyResponse.StatusCode);

            await using var errorStream = await applyResponse.Content.ReadAsStreamAsync();
            using var errorPayload = await JsonDocument.ParseAsync(errorStream);
            Assert.Equal("mode_validation_failed", errorPayload.RootElement.GetProperty("error").GetString());

            using var afterResponse = await client.GetAsync("/control/status");
            using var after = await TestJson.ReadOkAsync(afterResponse);
            Assert.Equal("audit", after.RootElement.GetProperty("mode").GetString());
            Assert.Equal(beforePolicyVersion, after.RootElement.GetProperty("policyVersion").GetString());
        }
        finally
        {
            ClearProxyWardEnvironment();
        }
    }

    [Fact]
    public async Task ControlYarpConfigAppliesAddedRouteForNewRequests()
    {
        await using var upstream = await TestUpstream.StartEchoAsync();
        var policyPath = TestFiles.SavePolicy(ValidYaml);
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", policyPath);
        Environment.SetEnvironmentVariable("PROXYWARD_CONTROL_ENABLED", "true");
        Environment.SetEnvironmentVariable("PROXYWARD_CONTROL_TOKEN", "test-control-token");

        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-control-token");

            using var applyResponse = await client.PutAsync(
                "/control/yarp-config",
                new StringContent(
                    CreateYarpConfigJson("dynamic", "/dynamic/mcp", $"{upstream.BaseAddress}/mcp"),
                    Encoding.UTF8,
                    "application/json"));

            Assert.Equal(HttpStatusCode.OK, applyResponse.StatusCode);

            await using var applyStream = await applyResponse.Content.ReadAsStreamAsync();
            using var applyPayload = await JsonDocument.ParseAsync(applyStream);
            Assert.Equal(2, applyPayload.RootElement.GetProperty("routeVersion").GetInt32());
            Assert.Equal(2, applyPayload.RootElement.GetProperty("routeCount").GetInt32());
            Assert.Equal(1, applyPayload.RootElement.GetProperty("clusterCount").GetInt32());

            using var proxiedResponse = await client.GetAsync("/dynamic/mcp/tools/list?cursor=abc");

            Assert.Equal(HttpStatusCode.OK, proxiedResponse.StatusCode);

            await using var stream = await proxiedResponse.Content.ReadAsStreamAsync();
            using var payload = await JsonDocument.ParseAsync(stream);
            Assert.Equal("/mcp/tools/list", payload.RootElement.GetProperty("path").GetString());
            Assert.Equal("?cursor=abc", payload.RootElement.GetProperty("query").GetString());
        }
        finally
        {
            ClearProxyWardEnvironment();
        }
    }

    [Fact]
    public async Task ControlYarpConfigAcceptsQueryValueTransforms()
    {
        await using var upstream = await TestUpstream.StartEchoAsync();
        var policyPath = TestFiles.SavePolicy(ValidYaml);
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", policyPath);
        Environment.SetEnvironmentVariable("PROXYWARD_CONTROL_ENABLED", "true");
        Environment.SetEnvironmentVariable("PROXYWARD_CONTROL_TOKEN", "test-control-token");

        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-control-token");

            using var applyResponse = await client.PutAsync(
                "/control/yarp-config",
                new StringContent(
                    CreateYarpConfigJsonWithQueryTransforms("dynamic", "/dynamic/mcp", $"{upstream.BaseAddress}/mcp"),
                    Encoding.UTF8,
                    "application/json"));

            Assert.Equal(HttpStatusCode.OK, applyResponse.StatusCode);

            using var proxiedResponse = await client.GetAsync("/dynamic/mcp/tools/list?cursor=abc");

            Assert.Equal(HttpStatusCode.OK, proxiedResponse.StatusCode);

            await using var stream = await proxiedResponse.Content.ReadAsStreamAsync();
            using var payload = await JsonDocument.ParseAsync(stream);
            var upstreamQuery = payload.RootElement.GetProperty("query").GetString();

            Assert.Equal("/mcp/tools/list", payload.RootElement.GetProperty("path").GetString());
            Assert.Contains("cursor=abc", upstreamQuery, StringComparison.Ordinal);
            Assert.Contains("login", upstreamQuery, StringComparison.Ordinal);
            Assert.Contains("gradio=none", upstreamQuery, StringComparison.Ordinal);
        }
        finally
        {
            ClearProxyWardEnvironment();
        }
    }

    [Fact]
    public async Task ControlYarpConfigRemovalStopsForwardingRemovedRoute()
    {
        await using var upstream = await TestUpstream.StartEchoAsync();
        var policyPath = TestFiles.SavePolicy(CreatePolicy(upstream.BaseAddress));
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", policyPath);
        Environment.SetEnvironmentVariable("PROXYWARD_CONTROL_ENABLED", "true");
        Environment.SetEnvironmentVariable("PROXYWARD_CONTROL_TOKEN", "test-control-token");

        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-control-token");

            using var beforeRemoval = await client.GetAsync("/sample/mcp/tools/list");
            Assert.Equal(HttpStatusCode.OK, beforeRemoval.StatusCode);

            using var applyResponse = await client.PutAsync(
                "/control/yarp-config",
                new StringContent("""{"routes":[],"clusters":[]}""", Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.OK, applyResponse.StatusCode);

            using var removedResponse = await client.GetAsync("/sample/mcp/tools/list");

            Assert.Equal(HttpStatusCode.NotFound, removedResponse.StatusCode);
        }
        finally
        {
            ClearProxyWardEnvironment();
        }
    }

    [Fact]
    public async Task ControlYarpConfigRejectsInvalidConfigAndPreservesActiveConfig()
    {
        await using var upstream = await TestUpstream.StartEchoAsync();
        var policyPath = TestFiles.SavePolicy(CreatePolicy(upstream.BaseAddress));
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", policyPath);
        Environment.SetEnvironmentVariable("PROXYWARD_CONTROL_ENABLED", "true");
        Environment.SetEnvironmentVariable("PROXYWARD_CONTROL_TOKEN", "test-control-token");

        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-control-token");

            using var applyResponse = await client.PutAsync(
                "/control/yarp-config",
                new StringContent(
                    """
                    {
                      "routes": [
                        {
                          "routeId": "broken-exact",
                          "clusterId": "missing",
                          "order": 0,
                          "match": { "path": "/broken/mcp" }
                        }
                      ],
                      "clusters": []
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"));

            Assert.Equal(HttpStatusCode.BadRequest, applyResponse.StatusCode);

            await using var errorStream = await applyResponse.Content.ReadAsStreamAsync();
            using var errorPayload = await JsonDocument.ParseAsync(errorStream);
            Assert.Equal("yarp_config_validation_failed", errorPayload.RootElement.GetProperty("error").GetString());

            using var preservedResponse = await client.GetAsync("/sample/mcp/tools/list");

            Assert.Equal(HttpStatusCode.OK, preservedResponse.StatusCode);
        }
        finally
        {
            ClearProxyWardEnvironment();
        }
    }

    [Fact]
    public async Task ControlYarpConfigRejectsUnsupportedTransformsBeforeReplacingConfig()
    {
        await using var upstream = await TestUpstream.StartEchoAsync();
        var policyPath = TestFiles.SavePolicy(CreatePolicy(upstream.BaseAddress));
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", policyPath);
        Environment.SetEnvironmentVariable("PROXYWARD_CONTROL_ENABLED", "true");
        Environment.SetEnvironmentVariable("PROXYWARD_CONTROL_TOKEN", "test-control-token");

        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-control-token");

            using var applyResponse = await client.PutAsync(
                "/control/yarp-config",
                new StringContent(
                    """
                    {
                      "routes": [
                        {
                          "routeId": "dynamic-exact",
                          "clusterId": "dynamic",
                          "order": 0,
                          "match": { "path": "/dynamic/mcp" },
                          "transforms": [ { "BogusTransform": "/dynamic/mcp" } ]
                        }
                      ],
                      "clusters": [
                        {
                          "clusterId": "dynamic",
                          "destinations": {
                            "primary": { "address": "http://127.0.0.1:65530/" }
                          }
                        }
                      ]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"));

            Assert.Equal(HttpStatusCode.BadRequest, applyResponse.StatusCode);

            using var preservedResponse = await client.GetAsync("/sample/mcp/tools/list");

            Assert.Equal(HttpStatusCode.OK, preservedResponse.StatusCode);
        }
        finally
        {
            ClearProxyWardEnvironment();
        }
    }

    [Fact]
    public async Task ProxyDataPlaneDoesNotServeDashboardAuditApi()
    {
        var policyPath = TestFiles.SavePolicy(ValidYaml);
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", policyPath);
        Environment.SetEnvironmentVariable("PROXYWARD_CONTROL_ENABLED", "true");
        Environment.SetEnvironmentVariable("PROXYWARD_CONTROL_TOKEN", "test-control-token");

        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/api/audit/events");

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        finally
        {
            ClearProxyWardEnvironment();
        }
    }

    private static string CreateYarpConfigJson(string clusterId, string routePrefix, string upstream) =>
        $$"""
        {
          "routes": [
            {
              "routeId": "{{clusterId}}-exact",
              "clusterId": "{{clusterId}}",
              "order": 0,
              "match": { "path": "{{routePrefix}}" },
              "transforms": [
                { "PathRemovePrefix": "{{routePrefix}}" },
                { "PathPrefix": "{{new Uri(upstream).AbsolutePath.TrimEnd('/')}}" }
              ]
            },
            {
              "routeId": "{{clusterId}}-catch-all",
              "clusterId": "{{clusterId}}",
              "order": 1,
              "match": { "path": "{{routePrefix}}/{**catchAll}" },
              "transforms": [
                { "PathRemovePrefix": "{{routePrefix}}" },
                { "PathPrefix": "{{new Uri(upstream).AbsolutePath.TrimEnd('/')}}" }
              ]
            }
          ],
          "clusters": [
            {
              "clusterId": "{{clusterId}}",
              "destinations": {
                "primary": { "address": "{{CreateDestinationAddress(upstream)}}" }
              }
            }
          ]
        }
        """;

    private static string CreateYarpConfigJsonWithQueryTransforms(string clusterId, string routePrefix, string upstream) =>
        $$"""
        {
          "routes": [
            {
              "routeId": "{{clusterId}}-exact",
              "clusterId": "{{clusterId}}",
              "order": 0,
              "match": { "path": "{{routePrefix}}" },
              "transforms": [
                { "PathRemovePrefix": "{{routePrefix}}" },
                { "PathPrefix": "{{new Uri(upstream).AbsolutePath.TrimEnd('/')}}" },
                { "QueryValueParameter": "login", "Set": "" },
                { "QueryValueParameter": "gradio", "Set": "none" }
              ]
            },
            {
              "routeId": "{{clusterId}}-catch-all",
              "clusterId": "{{clusterId}}",
              "order": 1,
              "match": { "path": "{{routePrefix}}/{**catchAll}" },
              "transforms": [
                { "PathRemovePrefix": "{{routePrefix}}" },
                { "PathPrefix": "{{new Uri(upstream).AbsolutePath.TrimEnd('/')}}" },
                { "QueryValueParameter": "login", "Set": "" },
                { "QueryValueParameter": "gradio", "Set": "none" }
              ]
            }
          ],
          "clusters": [
            {
              "clusterId": "{{clusterId}}",
              "destinations": {
                "primary": { "address": "{{CreateDestinationAddress(upstream)}}" }
              }
            }
          ]
        }
        """;

    private static string CreateDestinationAddress(string upstream)
    {
        var uri = new Uri(upstream);
        var builder = new UriBuilder(uri)
        {
            Path = "/",
            Query = string.Empty,
            Fragment = string.Empty
        };

        return builder.Uri.AbsoluteUri;
    }

    private static string CreatePolicy(string upstreamBaseAddress) =>
        ValidYaml.Replace("http://localhost:8080/mcp", $"{upstreamBaseAddress}/mcp", StringComparison.Ordinal);

    private static void ClearProxyWardEnvironment()
    {
        Environment.SetEnvironmentVariable("PROXYWARD_DB_PATH", null);
        Environment.SetEnvironmentVariable("PROXYWARD_CONTROL_ENABLED", null);
        Environment.SetEnvironmentVariable("PROXYWARD_CONTROL_TOKEN", null);
        Environment.SetEnvironmentVariable("PROXYWARD_ADMIN_TOKEN", null);
    }

    private static async Task<IReadOnlyList<ControlAuthFailureAuditRow>> ReadControlAuthFailureAuditRowsAsync(
        string databasePath) =>
        await AuditDatabase.ReadEventuallyAsync(() =>
        {
            var rows = new List<ControlAuthFailureAuditRow>();
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly
            }.ToString());
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT reasons, duration_ms, payload_json
                FROM audit_events
                WHERE event_type = 'proxy_control_auth_failure'
                ORDER BY id ASC;
                """;

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new ControlAuthFailureAuditRow(reader.GetString(0), reader.GetInt64(1), reader.GetString(2)));
            }

            return rows;
        });

    private sealed record ControlAuthFailureAuditRow(string Reasons, long DurationMs, string PayloadJson);

    private const string ValidYaml = """
        mode: audit
        inspection:
          maxBodyBytes: 1048576
          unsupportedStreaming: warn
        audit:
          sink: sqlite
          sqlitePath: ./data/proxyward.db
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
          sample:
            route: /sample/mcp
            upstream: http://localhost:8080/mcp
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
}
