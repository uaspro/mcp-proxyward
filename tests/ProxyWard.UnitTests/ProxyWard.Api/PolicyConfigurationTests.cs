using Microsoft.Data.Sqlite;
using ProxyWard.Policy.Configuration;
using ProxyWard.Policy.Persistence;

namespace ProxyWard.UnitTests;

public class PolicyConfigurationTests
{
    [Fact]
    public void LoadValidYamlReturnsStronglyTypedPolicy()
    {
        var policy = ProxyWardPolicyLoader.Load(ValidYaml);

        Assert.Equal(ProxyWardMode.Audit, policy.Mode);
        Assert.Equal(1_048_576, policy.Inspection.MaxBodyBytes);
        Assert.Equal(UnsupportedInspectionBehavior.Warn, policy.Inspection.UnsupportedStreaming);
        Assert.Equal(BatchToolCallBehavior.FailClosed, policy.Inspection.BatchToolCalls);
        Assert.True(policy.Audit.Enabled);
        Assert.Equal("mcp-proxyward", policy.Observability.ServiceName);
        Assert.Equal("APPLICATIONINSIGHTS_CONNECTION_STRING", policy.Observability.ApplicationInsights.ConnectionStringEnv);
        Assert.DoesNotContain(
            typeof(ProxyWardPolicy).GetProperties(),
            property => string.Equals(property.Name, "Lockfile", StringComparison.Ordinal));
        Assert.True(policy.Servers["sample"].Allowed);
        Assert.Equal("/sample/mcp", policy.Servers["sample"].Route);
        Assert.Equal(new Uri("http://localhost:8080/mcp"), policy.Servers["sample"].Upstream);
        Assert.Equal(ToolDefaultMode.Deny, policy.Servers["sample"].Tools.Default);
        Assert.True(policy.Servers["sample"].Secrets.RedactInLogs);
        Assert.False(policy.Servers["sample"].Secrets.BlockReturn);
        Assert.Empty(policy.Servers["sample"].Secrets.Patterns);
        Assert.StartsWith("sha256:", policy.VersionHash, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadAuditEnabledFalseReturnsTypedPolicyAndAffectsVersionHash()
    {
        var yaml = ValidYaml.Replace(
            "  enabled: true",
            "  enabled: false",
            StringComparison.Ordinal);

        var baseline = ProxyWardPolicyLoader.Load(ValidYaml);
        var policy = ProxyWardPolicyLoader.Load(yaml);

        Assert.False(policy.Audit.Enabled);
        Assert.NotEqual(baseline.VersionHash, policy.VersionHash);

        var serialized = ProxyWardPolicySerializer.ToYaml(policy);
        Assert.Contains("enabled: false", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("sink", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("sqlitePath", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadRuntimeAuditStorageFieldsThrowsClearValidationException()
    {
        var yaml = ValidYaml.Replace("enabled: true", "sink: postgres", StringComparison.Ordinal);

        var ex = Assert.Throws<PolicyValidationException>(() =>
            ProxyWardPolicyLoader.Load(yaml));

        Assert.Contains(ProxyWardPolicyLoader.RuntimeAuditStorageMessage, ex.Message);
    }

    [Fact]
    public void LoadStoredSnapshotMigratesLegacyAuditStorageFields()
    {
        var yaml = ValidYaml.Replace(
            "enabled: true",
            """
            sink: sqlite
              sqlitePath: ./data/proxyward.db
            """,
            StringComparison.Ordinal);

        var policy = ProxyWardPolicyLoader.LoadStoredSnapshot(yaml);
        var serialized = ProxyWardPolicySerializer.ToYaml(policy);

        Assert.True(policy.Audit.Enabled);
        Assert.Contains("enabled: true", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("sink", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("sqlitePath", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadStoredSnapshotMapsLegacyNoneSinkToAuditDisabled()
    {
        var yaml = ValidYaml.Replace("enabled: true", "sink: none", StringComparison.Ordinal);

        var policy = ProxyWardPolicyLoader.LoadStoredSnapshot(yaml);

        Assert.False(policy.Audit.Enabled);
    }

    [Fact]
    public async Task SqlitePolicyStoreNormalizesLegacyStoredSnapshotHash()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"proxyward-policy-{Guid.NewGuid():N}.db");
        try
        {
            var store = new SqlitePolicyStore(dbPath);
            await store.EnsureSchemaAsync();
            var legacyYaml = ValidYaml.Replace("enabled: true", "sink: none", StringComparison.Ordinal);

            await using (var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = dbPath,
                    Mode = SqliteOpenMode.ReadWriteCreate
                }.ToString()))
            {
                await connection.OpenAsync();
                await using var insert = connection.CreateCommand();
                insert.CommandText = """
                    INSERT INTO policy_snapshots (
                        created_at_utc,
                        policy_hash,
                        yaml,
                        requested_by,
                        note
                    ) VALUES (
                        $created_at_utc,
                        $policy_hash,
                        $yaml,
                        $requested_by,
                        $note
                    );
                    """;
                insert.Parameters.AddWithValue("$created_at_utc", DateTimeOffset.UtcNow.UtcDateTime.ToString("o"));
                insert.Parameters.AddWithValue("$policy_hash", "sha256:legacy");
                insert.Parameters.AddWithValue("$yaml", legacyYaml);
                insert.Parameters.AddWithValue("$requested_by", "test");
                insert.Parameters.AddWithValue("$note", "legacy snapshot");
                await insert.ExecuteNonQueryAsync();
            }

            var current = await store.ReadCurrentAsync();

            Assert.NotNull(current);
            Assert.False(current!.Policy.Audit.Enabled);
            Assert.Equal(current.Policy.VersionHash, current.PolicyHash);
            Assert.DoesNotContain("sink", current.Yaml, StringComparison.Ordinal);

            await using var verify = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = dbPath,
                    Mode = SqliteOpenMode.ReadOnly
                }.ToString());
            await verify.OpenAsync();
            await using var select = verify.CreateCommand();
            select.CommandText = "SELECT policy_hash, yaml FROM policy_snapshots ORDER BY id DESC LIMIT 1;";
            await using var reader = await select.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(current.PolicyHash, reader.GetString(0));
            Assert.Equal(current.Yaml, reader.GetString(1));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var path in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }

    [Fact]
    public void LoadExplicitFailClosedBatchToolCallBehaviorReturnsPolicy()
    {
        var yaml = ValidYaml.Replace(
            "unsupportedStreaming: warn",
            "unsupportedStreaming: warn\n  batchToolCalls: failClosed",
            StringComparison.Ordinal);

        var policy = ProxyWardPolicyLoader.Load(yaml);

        Assert.Equal(BatchToolCallBehavior.FailClosed, policy.Inspection.BatchToolCalls);
    }

    [Fact]
    public void LoadUnsupportedBatchToolCallBehaviorThrowsClearValidationException()
    {
        var yaml = ValidYaml.Replace(
            "unsupportedStreaming: warn",
            "unsupportedStreaming: warn\n  batchToolCalls: allowMixed",
            StringComparison.Ordinal);

        var ex = Assert.Throws<PolicyValidationException>(() =>
            ProxyWardPolicyLoader.Load(yaml));

        Assert.Contains("inspection.batchToolCalls must be 'failClosed'", ex.Message);
    }

    [Fact]
    public void LoadIncompleteYamlThrowsClearValidationException()
    {
        var ex = Assert.Throws<PolicyValidationException>(() =>
            ProxyWardPolicyLoader.Load("""
            mode: audit
            """));

        Assert.Contains("inspection section is required", ex.Message);
        Assert.Contains("audit section is required", ex.Message);
        Assert.Contains("observability section is required", ex.Message);
    }

    [Fact]
    public void LoadEmptyServersReturnsPolicy()
    {
        var policy = ProxyWardPolicyLoader.Load(EmptyServersYaml);

        Assert.Empty(policy.Servers);
    }

    [Fact]
    public void LoadYamlContainingRemovedLockfileKeyThrowsClearValidationException()
    {
        var yaml = ValidYaml.Replace(
            "servers:",
            "lockfile: ./proxyward.lock.yaml\nservers:",
            StringComparison.Ordinal);

        var ex = Assert.Throws<PolicyValidationException>(() =>
            ProxyWardPolicyLoader.Load(yaml));

        Assert.Equal(ProxyWardPolicyLoader.RemovedLockfileMessage, Assert.Single(ex.Errors));
        Assert.Contains(ProxyWardPolicyLoader.RemovedLockfileMessage, ex.Message);
    }

    [Fact]
    public void LoadHiddenToolPolicyReturnsTypedPolicyAndAffectsVersionHash()
    {
        var yaml = ValidYaml.Replace(
            "      block: []",
            """
                  block: []
                  hide:
                    - repos.archive
            """,
            StringComparison.Ordinal);

        var baseline = ProxyWardPolicyLoader.Load(ValidYaml);
        var policy = ProxyWardPolicyLoader.Load(yaml);

        Assert.Equal(["repos.archive"], policy.Servers["sample"].Tools.Hide);
        Assert.NotEqual(baseline.VersionHash, policy.VersionHash);
        Assert.Contains("hide", ProxyWardPolicySerializer.ToYaml(policy), StringComparison.Ordinal);
    }

    [Fact]
    public void LoadHideToolDefaultReturnsTypedPolicyAndAffectsVersionHash()
    {
        var yaml = ValidYaml.Replace("default: deny", "default: hide", StringComparison.Ordinal);

        var baseline = ProxyWardPolicyLoader.Load(ValidYaml);
        var policy = ProxyWardPolicyLoader.Load(yaml);

        Assert.Equal(ToolDefaultMode.Hide, policy.Servers["sample"].Tools.Default);
        Assert.NotEqual(baseline.VersionHash, policy.VersionHash);
        Assert.Contains("default: hide", ProxyWardPolicySerializer.ToYaml(policy), StringComparison.Ordinal);
    }

    [Fact]
    public void PolicyVersionHashIsStableForRepeatedLoads()
    {
        var first = ProxyWardPolicyLoader.Load(ValidYaml);
        var second = ProxyWardPolicyLoader.Load(ValidYaml);

        Assert.Equal(first.VersionHash, second.VersionHash);
    }

    [Fact]
    public void LoadToolArgumentOverridesReturnsTypedPolicy()
    {
        var yaml = ValidYamlWithOverrides(
            """
            overrides:
              fs.safe-read:
                paths:
                  allowedRoots: []
                  blockTraversal: false
                commands:
                  dangerous: []
            """);

        var policy = ProxyWardPolicyLoader.Load(yaml);

        var overrides = policy.Servers["sample"].Arguments.Overrides;
        var toolOverride = Assert.Contains("fs.safe-read", overrides);
        Assert.NotNull(toolOverride.Paths);
        Assert.Empty(toolOverride.Paths.AllowedRoots!);
        Assert.False(toolOverride.Paths.BlockTraversal!.Value);
        Assert.Null(toolOverride.Hosts);
        Assert.NotNull(toolOverride.Commands);
        Assert.Empty(toolOverride.Commands.Dangerous!);
        Assert.Null(toolOverride.Commands.BlockShell);
    }

    [Fact]
    public void LoadNoOpToolArgumentOverrideThrowsClearValidationException()
    {
        var yaml = ValidYamlWithOverrides(
            """
            overrides:
              fs.safe-read:
                paths: {}
            """);

        var ex = Assert.Throws<PolicyValidationException>(() =>
            ProxyWardPolicyLoader.Load(yaml));

        Assert.Contains(
            "servers.sample.arguments.overrides.fs.safe-read must define at least one argument rule override",
            ex.Message);
    }

    [Fact]
    public void LoadEmptyToolArgumentOverrideNameThrowsClearValidationException()
    {
        var yaml = ValidYamlWithOverrides(
            """
            overrides:
              "":
                paths:
                  blockTraversal: false
            """);

        var ex = Assert.Throws<PolicyValidationException>(() =>
            ProxyWardPolicyLoader.Load(yaml));

        Assert.Contains(
            "servers.sample.arguments.overrides contains an empty tool name",
            ex.Message);
    }

    [Fact]
    public void LoadNullToolArgumentOverrideThrowsClearValidationException()
    {
        var yaml = ValidYamlWithOverrides(
            """
            overrides:
              fs.safe-read:
            """);

        var ex = Assert.Throws<PolicyValidationException>(() =>
            ProxyWardPolicyLoader.Load(yaml));

        Assert.Contains(
            "servers.sample.arguments.overrides.fs.safe-read section is required",
            ex.Message);
    }

    [Fact]
    public void PolicyVersionHashChangesWhenToolArgumentOverrideChanges()
    {
        var firstYaml = ValidYamlWithOverrides(
            """
            overrides:
              fs.safe-read:
                paths:
                  blockTraversal: false
            """);
        var secondYaml = firstYaml.Replace("blockTraversal: false", "blockTraversal: true", StringComparison.Ordinal);

        var first = ProxyWardPolicyLoader.Load(firstYaml);
        var second = ProxyWardPolicyLoader.Load(secondYaml);

        Assert.NotEqual(first.VersionHash, second.VersionHash);
    }

    [Fact]
    public void LoadSecretsPolicyReturnsTypedPolicy()
    {
        var policy = ProxyWardPolicyLoader.Load(ValidYamlWithSecrets(
            """
            secrets:
              redactInLogs: true
              blockReturn: true
              patterns:
                - ghp_
                - /github_pat_[A-Za-z0-9_]+/
            """));

        var secrets = policy.Servers["sample"].Secrets;
        Assert.True(secrets.RedactInLogs);
        Assert.True(secrets.BlockReturn);
        Assert.Equal(["/github_pat_[A-Za-z0-9_]+/", "ghp_"], secrets.Patterns);
    }

    [Fact]
    public void LoadInvalidSecretsRegexThrowsClearValidationException()
    {
        var yaml = ValidYamlWithSecrets(
            """
            secrets:
              patterns:
                - /github_pat_(/
            """);

        var ex = Assert.Throws<PolicyValidationException>(() =>
            ProxyWardPolicyLoader.Load(yaml));

        Assert.Contains("servers.sample.secrets.patterns[0] is not a valid regex", ex.Message);
    }

    [Fact]
    public void LoadOverbroadSecretsRegexThrowsClearValidationException()
    {
        var yaml = ValidYamlWithSecrets(
            """
            secrets:
              patterns:
                - /.*/
            """);

        var ex = Assert.Throws<PolicyValidationException>(() =>
            ProxyWardPolicyLoader.Load(yaml));

        Assert.Contains("servers.sample.secrets.patterns[0] is over-broad", ex.Message);
    }

    [Fact]
    public void PolicyVersionHashChangesWhenSecretsPolicyChanges()
    {
        var first = ProxyWardPolicyLoader.Load(ValidYamlWithSecrets(
            """
            secrets:
              redactInLogs: true
              blockReturn: false
              patterns:
                - ghp_
            """));
        var second = ProxyWardPolicyLoader.Load(ValidYamlWithSecrets(
            """
            secrets:
              redactInLogs: true
              blockReturn: true
              patterns:
                - ghp_
            """));

        Assert.NotEqual(first.VersionHash, second.VersionHash);
    }

    private static string ValidYamlWithOverrides(string overridesBlock) =>
        """
        mode: audit
        inspection:
          maxBodyBytes: 1048576
          unsupportedStreaming: warn
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
                  - curl
                  - wget
                  - nc
                  - powershell
                  - bash
        """ + "\n" + Indent(overridesBlock, 6);

    private static string ValidYamlWithSecrets(string secretsBlock) =>
        ValidYaml.Replace(
            "    tools:",
            Indent(secretsBlock, 4) + "\n    tools:",
            StringComparison.Ordinal);

    private static string Indent(string block, int spaces)
    {
        var prefix = new string(' ', spaces);
        var lines = block.ReplaceLineEndings("\n").Split('\n');
        return string.Join(
            "\n",
            lines
                .Where(line => line.Length > 0)
                .Select(line => prefix + line));
    }

    private const string ValidYaml = """
        mode: audit
        inspection:
          maxBodyBytes: 1048576
          unsupportedStreaming: warn
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
                  - curl
                  - wget
                  - nc
                  - powershell
                  - bash
        """;

    private const string EmptyServersYaml = """
        mode: audit
        inspection:
          maxBodyBytes: 1048576
          unsupportedStreaming: warn
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
        servers: {}
        """;
}
