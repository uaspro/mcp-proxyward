using ProxyWard.Policy.Configuration;

namespace ProxyWard.Policy.Engine;

public sealed class ArgumentPolicyOverrideResolver
{
    public ResolvedArgumentPolicy Resolve(ArgumentPolicy arguments, string? toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName)
            || !arguments.Overrides.TryGetValue(toolName, out var toolOverride))
        {
            return new ResolvedArgumentPolicy(
                arguments.Paths,
                arguments.Hosts,
                arguments.Commands,
                OverrideApplied: false,
                OverrideToolName: null);
        }

        return new ResolvedArgumentPolicy(
            ResolvePaths(arguments.Paths, toolOverride.Paths),
            ResolveHosts(arguments.Hosts, toolOverride.Hosts),
            ResolveCommands(arguments.Commands, toolOverride.Commands),
            OverrideApplied: true,
            OverrideToolName: toolOverride.ToolName);
    }

    private static PathArgumentPolicy ResolvePaths(
        PathArgumentPolicy server,
        PathArgumentPolicyOverride? toolOverride)
    {
        if (toolOverride is null)
        {
            return server;
        }

        return new PathArgumentPolicy(
            toolOverride.AllowedRoots ?? server.AllowedRoots,
            toolOverride.BlockTraversal ?? server.BlockTraversal);
    }

    private static HostArgumentPolicy ResolveHosts(
        HostArgumentPolicy server,
        HostArgumentPolicyOverride? toolOverride)
    {
        if (toolOverride is null)
        {
            return server;
        }

        return new HostArgumentPolicy(
            toolOverride.Allow ?? server.Allow,
            toolOverride.BlockPrivateNetworks ?? server.BlockPrivateNetworks);
    }

    private static CommandArgumentPolicy ResolveCommands(
        CommandArgumentPolicy server,
        CommandArgumentPolicyOverride? toolOverride)
    {
        if (toolOverride is null)
        {
            return server;
        }

        return new CommandArgumentPolicy(
            toolOverride.BlockShell ?? server.BlockShell,
            toolOverride.Dangerous ?? server.Dangerous);
    }
}

public sealed record ResolvedArgumentPolicy(
    PathArgumentPolicy Paths,
    HostArgumentPolicy Hosts,
    CommandArgumentPolicy Commands,
    bool OverrideApplied,
    string? OverrideToolName);
