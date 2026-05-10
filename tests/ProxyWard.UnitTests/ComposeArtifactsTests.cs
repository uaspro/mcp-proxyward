using ProxyWard.Policy.Configuration;

namespace ProxyWard.UnitTests;

public class ComposeArtifactsTests
{
    [Fact]
    public void DockerComposeArtifactsExistAndSolutionIncludesSampleServer()
    {
        AssertFileExists("Dockerfile");
        AssertFileExists(".dockerignore");
        AssertFileExists("docker-compose.yml");
        AssertFileExists("samples/compose/otel-collector.yaml");
        AssertFileExists("samples/compose/README.md");
        AssertFileExists("samples/SampleMcpServer/Program.cs");
        AssertFileExists("samples/SampleMcpServer/SampleMcpServer.csproj");
        AssertFileExists("samples/SampleMcpServer/Dockerfile");

        var solution = ReadRepoFile("McpProxyWard.slnx");

        Assert.Contains(
            "samples/SampleMcpServer/SampleMcpServer.csproj",
            solution,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultPolicyBootstrapsSampleRouteStorageAndTelemetrySettings()
    {
        var policy = ProxyWardPolicyLoader.Load(
            ProxyWardDefaultPolicy.CreateYaml("/app/data/proxyward.db", "http://sample-mcp:8080/mcp"));

        Assert.Equal(ProxyWardMode.Audit, policy.Mode);
        Assert.Equal("sqlite", policy.Audit.Sink);
        Assert.Equal("/app/data/proxyward.db", policy.Audit.SqlitePath);
        Assert.True(policy.Observability.Otlp.Enabled);
        Assert.Equal("http://otel-collector:4317", policy.Observability.Otlp.Endpoint);

        var server = Assert.Contains("sample", policy.Servers);
        Assert.True(server.Allowed);
        Assert.Equal("/sample/mcp", server.Route);
        Assert.Equal(new Uri("http://sample-mcp:8080/mcp"), server.Upstream);
        Assert.Equal(ToolDefaultMode.Allow, server.Tools.Default);
        Assert.False(server.Arguments.Paths.BlockTraversal);
        Assert.False(server.Arguments.Hosts.BlockPrivateNetworks);
        Assert.False(server.Arguments.Commands.BlockShell);
    }

    [Fact]
    public void DockerComposeWiresProxyWardSampleMcpCollectorAndDataVolume()
    {
        var compose = ReadRepoFile("docker-compose.yml");

        Assert.Contains("proxyward:", compose, StringComparison.Ordinal);
        Assert.Contains("sample-mcp:", compose, StringComparison.Ordinal);
        Assert.Contains("otel-collector:", compose, StringComparison.Ordinal);
        Assert.Contains("proxyward-data:", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("proxyward.yaml:/app/config/proxyward.yaml", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("PROXYWARD_POLICY_PATH", compose, StringComparison.Ordinal);
        Assert.Contains("PROXYWARD_DB_PATH: /app/data/proxyward.db", compose, StringComparison.Ordinal);
        Assert.Contains("http://+:8080", compose, StringComparison.Ordinal);
    }

    [Fact]
    public void DockerContextIgnoresGeneratedBmadAndBuildOutputWithoutIgnoringSamples()
    {
        var dockerignore = ReadRepoFile(".dockerignore");

        Assert.Contains("_bmad/", dockerignore, StringComparison.Ordinal);
        Assert.Contains("_bmad-output/", dockerignore, StringComparison.Ordinal);
        Assert.Contains(".agents/", dockerignore, StringComparison.Ordinal);
        Assert.Contains("**/bin/", dockerignore, StringComparison.Ordinal);
        Assert.Contains("**/obj/", dockerignore, StringComparison.Ordinal);
        Assert.DoesNotContain("samples/", dockerignore, StringComparison.Ordinal);
    }

    [Fact]
    public void SampleMcpServerPublishesEchoToolContract()
    {
        var program = ReadRepoFile("samples/SampleMcpServer/Program.cs");
        var readme = ReadRepoFile("samples/compose/README.md");

        Assert.Contains("[\"name\"] = \"echo\"", program, StringComparison.Ordinal);
        Assert.Contains("[\"description\"] = \"Returns the supplied message.\"", program, StringComparison.Ordinal);
        Assert.Contains("\"method\":\"tools/call\"", readme, StringComparison.Ordinal);
        Assert.Contains("\"name\":\"echo\"", readme, StringComparison.Ordinal);
    }

    private static void AssertFileExists(string relativePath) =>
        Assert.True(File.Exists(RepoPath(relativePath)), $"{relativePath} should exist.");

    private static string ReadRepoFile(string relativePath) =>
        File.ReadAllText(RepoPath(relativePath));

    private static string RepoPath(string relativePath) =>
        Path.Combine(RepoRoot.Value, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static readonly Lazy<string> RepoRoot = new(FindRepoRoot);

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "McpProxyWard.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not find repository root from test output directory.");
    }
}
