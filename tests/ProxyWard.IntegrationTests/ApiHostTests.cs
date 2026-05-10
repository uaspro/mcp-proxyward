using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using ProxyWard.Policy.Configuration;
using ProxyWard.Policy.Persistence;

namespace ProxyWard.IntegrationTests;

public class ApiHostTests
{
    private const string DbEnv = "PROXYWARD_DB_PATH";

    [Fact]
    public async Task HealthEndpointIncludesLoadedPolicyMetadata()
    {
        var databasePath = TestFiles.NewSqlitePath();
        await new SqlitePolicyStore(databasePath).SaveAsync(ValidYaml);
        Environment.SetEnvironmentVariable(DbEnv, databasePath);

        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/health");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var payload = await TestJson.ReadAsync(response);

            Assert.Equal("healthy", payload.RootElement.GetProperty("status").GetString());
            Assert.Equal("audit", payload.RootElement.GetProperty("mode").GetString());
            Assert.Equal(1, payload.RootElement.GetProperty("serverCount").GetInt32());
            Assert.StartsWith("sha256:", payload.RootElement.GetProperty("policyVersion").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(DbEnv, null);
            TestFiles.DeleteSqlite(databasePath);
        }
    }

    [Fact]
    public void InvalidPolicyPreventsHostStartup()
    {
        var databasePath = TestFiles.NewSqlitePath();
        InsertRawPolicy(databasePath, """
            mode: audit
            """);
        Environment.SetEnvironmentVariable(DbEnv, databasePath);

        try
        {
            using var factory = new WebApplicationFactory<Program>();

            var ex = Assert.Throws<PolicyValidationException>(() => factory.CreateClient());
            Assert.Contains("Invalid ProxyWard policy", ex.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable(DbEnv, null);
            TestFiles.DeleteSqlite(databasePath);
        }
    }

    [Fact]
    public void RemovedLockfileKeyPreventsHostStartup()
    {
        var databasePath = TestFiles.NewSqlitePath();
        InsertRawPolicy(databasePath, ValidYaml.Replace(
            "servers:",
            "lockfile: ./proxyward.lock.yaml\nservers:",
            StringComparison.Ordinal));
        Environment.SetEnvironmentVariable(DbEnv, databasePath);

        try
        {
            using var factory = new WebApplicationFactory<Program>();

            var ex = Assert.Throws<PolicyValidationException>(() => factory.CreateClient());
            Assert.Contains(ProxyWardPolicyLoader.RemovedLockfileMessage, ex.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable(DbEnv, null);
            TestFiles.DeleteSqlite(databasePath);
        }
    }

    private static void InsertRawPolicy(string databasePath, string yaml)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString());
        connection.Open();
        using (var schema = connection.CreateCommand())
        {
            schema.CommandText = """
                CREATE TABLE policy_snapshots (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    created_at_utc TEXT NOT NULL,
                    policy_hash TEXT NOT NULL,
                    yaml TEXT NOT NULL,
                    requested_by TEXT NULL,
                    note TEXT NULL
                );
                """;
            schema.ExecuteNonQuery();
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO policy_snapshots (created_at_utc, policy_hash, yaml)
            VALUES ($created_at_utc, $policy_hash, $yaml);
            """;
        command.Parameters.AddWithValue("$created_at_utc", DateTimeOffset.UtcNow.UtcDateTime.ToString("o"));
        command.Parameters.AddWithValue("$policy_hash", "sha256:invalid");
        command.Parameters.AddWithValue("$yaml", yaml);
        command.ExecuteNonQuery();
    }

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
