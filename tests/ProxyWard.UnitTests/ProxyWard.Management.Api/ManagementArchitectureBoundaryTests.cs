using System.Xml.Linq;
using ProxyWard.Management.Application;

namespace ProxyWard.UnitTests;

public class ManagementArchitectureBoundaryTests
{
    [Fact]
    public void ManagementApplicationDoesNotReferenceInfrastructureAdapters()
    {
        var references = typeof(ManagementApiOptions)
            .Assembly
            .GetReferencedAssemblies()
            .Select(assembly => assembly.Name)
            .ToArray();

        Assert.DoesNotContain("Microsoft.Data.Sqlite", references);
        Assert.DoesNotContain("YamlDotNet", references);
        Assert.DoesNotContain("ProxyWard.Management.Api", references);
        Assert.DoesNotContain("ProxyWard.Management.Infrastructure", references);
    }

    [Fact]
    public void ManagementApiDoesNotReferenceAuditStorageDirectly()
    {
        var projectReferences = XDocument
            .Load(RepoPath("src/ProxyWard.Management.Api/ProxyWard.Management.Api.csproj"))
            .Descendants("ProjectReference")
            .Select(reference => reference.Attribute("Include")?.Value)
            .Where(include => include is not null)
            .Select(include => include!)
            .ToArray();

        var sourceOffenders = Directory
            .EnumerateFiles(
                RepoPath("src/ProxyWard.Management.Api"),
                "*.cs",
                SearchOption.AllDirectories)
            .Where(file => File.ReadAllText(file).Contains("ProxyWard.Audit", StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(RepoRoot.Value, file))
            .ToArray();

        Assert.DoesNotContain(
            projectReferences,
            include => include.Contains("ProxyWard.Audit", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(sourceOffenders);
    }

    [Fact]
    public void ManagementApplicationSourceDoesNotUseConcretePolicyPersistence()
    {
        var offenders = Directory
            .EnumerateFiles(
                RepoPath("src/ProxyWard.Management.Application"),
                "*.cs",
                SearchOption.AllDirectories)
            .Where(file =>
            {
                var source = File.ReadAllText(file);
                return source.Contains("ProxyWard.Policy.Persistence", StringComparison.Ordinal)
                    || source.Contains("SqlitePolicyStore", StringComparison.Ordinal);
            })
            .Select(file => Path.GetRelativePath(RepoRoot.Value, file))
            .ToArray();

        Assert.Empty(offenders);
    }

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
