using System.Xml.Linq;
using ProxyWard.Audit;
using ProxyWard.Audit.Events;
using ProxyWard.Core;
using ProxyWard.Core.Policies;
using ProxyWard.Locking;
using ProxyWard.Policy;

namespace ProxyWard.UnitTests;

public class ArchitectureBoundaryTests
{
    [Fact]
    public void SharedKernelHasNoOuterLayerReferences()
    {
        var references = AssemblyReferences(typeof(ProxyWardCoreMarker));

        Assert.DoesNotContain(references, IsInfrastructureOrPresentationReference);
        Assert.DoesNotContain(references, name => name is "ProxyWard.Proxy.Domain"
            or "ProxyWard.Proxy.Application"
            or "ProxyWard.Proxy.Infrastructure"
            or "ProxyWard.Api");
    }

    [Fact]
    public void ProxyDomainReferencesSharedKernelWithoutInfrastructureOrPresentation()
    {
        var references = AssemblyReferences(typeof(ProxyWardPolicyMarker));

        Assert.Contains("ProxyWard.SharedKernel", references);
        Assert.DoesNotContain("ProxyWard.Proxy.Application", references);
        Assert.DoesNotContain("ProxyWard.Proxy.Infrastructure", references);
        Assert.DoesNotContain(references, IsInfrastructureOrPresentationReference);
    }

    [Fact]
    public void ProxyApplicationReferencesDomainWithoutInfrastructureOrPresentation()
    {
        var references = AssemblyReferences(typeof(IAuditSink));

        Assert.Contains("ProxyWard.Proxy.Domain", references);
        Assert.DoesNotContain("ProxyWard.Proxy.Infrastructure", references);
        Assert.DoesNotContain(references, IsInfrastructureOrPresentationReference);
    }

    [Fact]
    public void ProxyApiDoesNotOwnApplicationRuntimeOrInfrastructureAdapterFolders()
    {
        var misplacedFiles = new[]
            {
                "src/ProxyWard.Api/Runtime",
                "src/ProxyWard.Api/Hosts",
                "src/ProxyWard.Api/Yarp"
            }
            .Where(path => Directory.Exists(RepoPath(path)))
            .SelectMany(path => Directory.EnumerateFiles(RepoPath(path), "*.cs", SearchOption.AllDirectories))
            .Select(file => Path.GetRelativePath(RepoRoot.Value, file))
            .ToArray();

        Assert.Empty(misplacedFiles);
    }

    [Fact]
    public void ProxyInfrastructureOwnsYarpAdapterDependency()
    {
        var packageReferences = XDocument
            .Load(RepoPath("src/ProxyWard.Proxy.Infrastructure/ProxyWard.Proxy.Infrastructure.csproj"))
            .Descendants("PackageReference")
            .Select(reference => reference.Attribute("Include")?.Value)
            .Where(include => include is not null)
            .Select(include => include!)
            .ToArray();

        Assert.Contains("Yarp.ReverseProxy", packageReferences);
    }

    [Fact]
    public void ProxyDomainAuditAndLockingMarkersLiveInDomainAssembly()
    {
        Assert.Equal(
            typeof(ProxyWardPolicyMarker).Assembly,
            typeof(ProxyWardAuditMarker).Assembly);
        Assert.Equal(
            typeof(ProxyWardPolicyMarker).Assembly,
            typeof(ProxyWardLockingMarker).Assembly);
    }

    [Fact]
    public void PolicyDecisionCanRepresentBlockReason()
    {
        var decision = PolicyDecision.Block(PolicyReasonCodes.ToolBlocked);

        Assert.Equal(PolicyDecisionType.Block, decision.Type);
        Assert.Contains(PolicyReasonCodes.ToolBlocked, decision.Reasons);
    }

    private static string?[] AssemblyReferences(Type marker) =>
        marker.Assembly
            .GetReferencedAssemblies()
            .Select(assembly => assembly.Name)
            .ToArray();

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

    private static bool IsInfrastructureOrPresentationReference(string? name) =>
        name is not null
        && (name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)
            || name.StartsWith("Yarp", StringComparison.Ordinal)
            || name is "Microsoft.Data.Sqlite"
            || name is "Npgsql"
            || name is "YamlDotNet");
}
