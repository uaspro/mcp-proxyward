using ProxyWard.Policy.Configuration;
using ProxyWard.Policy.Engine;

namespace ProxyWard.UnitTests;

public class ArgumentPolicyOverrideResolverTests
{
    [Fact]
    public void NoOverrideReturnsServerLevelRules()
    {
        var serverArguments = CreateServerArguments();
        var resolver = new ArgumentPolicyOverrideResolver();

        var resolved = resolver.Resolve(serverArguments, "fs.read");

        Assert.False(resolved.OverrideApplied);
        Assert.Equal(["/workspace"], resolved.Paths.AllowedRoots);
        Assert.True(resolved.Paths.BlockTraversal);
        Assert.Equal(["api.github.com"], resolved.Hosts.Allow);
        Assert.True(resolved.Hosts.BlockPrivateNetworks);
        Assert.True(resolved.Commands.BlockShell);
        Assert.Equal(["rm"], resolved.Commands.Dangerous);
    }

    [Fact]
    public void ExactToolOverrideCanRelaxPathRules()
    {
        var serverArguments = CreateServerArguments(
            overrides: new Dictionary<string, ToolArgumentPolicyOverride>(StringComparer.Ordinal)
            {
                ["fs.safe-read"] = new(
                    "fs.safe-read",
                    Paths: new PathArgumentPolicyOverride(AllowedRoots: [], BlockTraversal: false),
                    Hosts: null,
                    Commands: null)
            });
        var resolver = new ArgumentPolicyOverrideResolver();

        var resolved = resolver.Resolve(serverArguments, "fs.safe-read");

        Assert.True(resolved.OverrideApplied);
        Assert.Equal("fs.safe-read", resolved.OverrideToolName);
        Assert.Empty(resolved.Paths.AllowedRoots);
        Assert.False(resolved.Paths.BlockTraversal);
        Assert.Equal(["api.github.com"], resolved.Hosts.Allow);
        Assert.True(resolved.Hosts.BlockPrivateNetworks);
    }

    [Fact]
    public void PartialOverrideInheritsUnspecifiedFields()
    {
        var serverArguments = CreateServerArguments(
            overrides: new Dictionary<string, ToolArgumentPolicyOverride>(StringComparer.Ordinal)
            {
                ["http.fetch"] = new(
                    "http.fetch",
                    Paths: null,
                    Hosts: new HostArgumentPolicyOverride(Allow: [], BlockPrivateNetworks: false),
                    Commands: new CommandArgumentPolicyOverride(BlockShell: null, Dangerous: []))
            });
        var resolver = new ArgumentPolicyOverrideResolver();

        var resolved = resolver.Resolve(serverArguments, "http.fetch");

        Assert.True(resolved.OverrideApplied);
        Assert.Equal(["/workspace"], resolved.Paths.AllowedRoots);
        Assert.True(resolved.Paths.BlockTraversal);
        Assert.Empty(resolved.Hosts.Allow);
        Assert.False(resolved.Hosts.BlockPrivateNetworks);
        Assert.True(resolved.Commands.BlockShell);
        Assert.Empty(resolved.Commands.Dangerous);
    }

    [Fact]
    public void ToolNameMatchingIsExactAndCaseSensitive()
    {
        var serverArguments = CreateServerArguments(
            overrides: new Dictionary<string, ToolArgumentPolicyOverride>(StringComparer.Ordinal)
            {
                ["fs.read"] = new(
                    "fs.read",
                    Paths: new PathArgumentPolicyOverride(AllowedRoots: [], BlockTraversal: false),
                    Hosts: null,
                    Commands: null)
            });
        var resolver = new ArgumentPolicyOverrideResolver();

        var resolved = resolver.Resolve(serverArguments, "FS.READ");

        Assert.False(resolved.OverrideApplied);
        Assert.Equal(["/workspace"], resolved.Paths.AllowedRoots);
        Assert.True(resolved.Paths.BlockTraversal);
    }

    private static ArgumentPolicy CreateServerArguments(
        IReadOnlyDictionary<string, ToolArgumentPolicyOverride>? overrides = null) =>
        new(
            Paths: new PathArgumentPolicy(["/workspace"], BlockTraversal: true),
            Hosts: new HostArgumentPolicy(["api.github.com"], BlockPrivateNetworks: true),
            Commands: new CommandArgumentPolicy(BlockShell: true, Dangerous: ["rm"]),
            Overrides: overrides ?? new Dictionary<string, ToolArgumentPolicyOverride>(StringComparer.Ordinal));
}
