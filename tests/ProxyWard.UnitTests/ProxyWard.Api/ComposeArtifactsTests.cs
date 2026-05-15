using ProxyWard.Policy.Configuration;

namespace ProxyWard.UnitTests;

public class ComposeArtifactsTests
{
    [Fact]
    public void DockerComposeArtifactsExistAndSolutionOmitsSampleServer()
    {
        AssertFileExists("Dockerfile");
        AssertFileExists(".dockerignore");
        AssertFileExists("docker-compose.yml");
        AssertFileExists("samples/compose/otel-collector.yaml");
        AssertFileExists("samples/compose/README.md");

        var solution = ReadRepoFile("McpProxyWard.slnx");

        Assert.DoesNotContain(
            "samples/SampleMcpServer/SampleMcpServer.csproj",
            solution,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultPolicyBootstrapsEmptyRoutesStorageAndTelemetrySettings()
    {
        var policy = ProxyWardPolicyLoader.Load(
            ProxyWardDefaultPolicy.CreateYaml("/app/data/proxyward.db"));

        Assert.Equal(ProxyWardMode.Audit, policy.Mode);
        Assert.True(policy.Audit.Enabled);
        Assert.True(policy.Observability.Otlp.Enabled);
        Assert.Equal("http://otel-collector:4317", policy.Observability.Otlp.Endpoint);
        Assert.Empty(policy.Servers);
    }

    [Fact]
    public void DockerComposeWiresProxyWardCollectorAndDataVolume()
    {
        var compose = ReadRepoFile("docker-compose.yml");

        Assert.Contains("proxyward:", compose, StringComparison.Ordinal);
        Assert.Contains("otel-collector:", compose, StringComparison.Ordinal);
        Assert.Contains("proxyward-data:", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("sample-mcp:", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("PROXYWARD_BOOTSTRAP_SAMPLE_UPSTREAM", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("proxyward.yaml:/app/config/proxyward.yaml", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("PROXYWARD_POLICY_PATH", compose, StringComparison.Ordinal);
        Assert.Contains("PROXYWARD_DB_PATH: /app/data/proxyward.db", compose, StringComparison.Ordinal);
        Assert.Contains("http://+:8080", compose, StringComparison.Ordinal);
    }

    [Fact]
    public void DockerContextIgnoresGeneratedBmadAndBuildOutputWithoutIgnoringComposeSamples()
    {
        var dockerignore = ReadRepoFile(".dockerignore");

        Assert.Contains("_bmad/", dockerignore, StringComparison.Ordinal);
        Assert.Contains("_bmad-output/", dockerignore, StringComparison.Ordinal);
        Assert.Contains(".agents/", dockerignore, StringComparison.Ordinal);
        Assert.Contains("**/bin/", dockerignore, StringComparison.Ordinal);
        Assert.Contains("**/obj/", dockerignore, StringComparison.Ordinal);
        Assert.DoesNotContain("samples/", dockerignore, StringComparison.Ordinal);
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
