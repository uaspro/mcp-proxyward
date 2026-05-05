using ProxyWard.Core.Policies;
using ProxyWard.Policy.Configuration;

namespace ProxyWard.Policy.Engine;

public sealed class ToolPolicyEvaluator
{
    public PolicyDecision Evaluate(ProxyWardMode mode, ToolPolicy tools, string? toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return mode.AsBlockDecision(PolicyReasonCodes.ToolNotAllowed);
        }

        if (tools.Block.Contains(toolName, StringComparer.Ordinal))
        {
            return mode.AsBlockDecision(PolicyReasonCodes.ToolBlocked);
        }

        if (tools.Default == ToolDefaultMode.Allow)
        {
            return PolicyDecision.Allow();
        }

        if (tools.Allow.Contains(toolName, StringComparer.Ordinal))
        {
            return PolicyDecision.Allow();
        }

        return mode.AsBlockDecision(PolicyReasonCodes.ToolNotAllowed);
    }
}
