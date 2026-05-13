using ProxyWard.Core.Policies;
using ProxyWard.Policy.Configuration;
using ProxyWard.Policy.Engine;

namespace ProxyWard.UnitTests;

public class ToolPolicyEvaluatorTests
{
    [Fact]
    public void BlockedToolReturnsBlockEvenWhenAlsoInAllowListInEnforce()
    {
        var evaluator = new ToolPolicyEvaluator();
        var tools = CreateTools(ToolDefaultMode.Allow, allow: ["repos.search"], block: ["repos.search"]);

        var decision = evaluator.Evaluate(ProxyWardMode.Enforce, tools, "repos.search");

        Assert.Equal(PolicyDecisionType.Block, decision.Type);
        Assert.Contains(PolicyReasonCodes.ToolBlocked, decision.Reasons);
    }

    [Fact]
    public void BlockedToolInAuditModeReturnsWouldBlock()
    {
        var evaluator = new ToolPolicyEvaluator();
        var tools = CreateTools(ToolDefaultMode.Allow, allow: [], block: ["shell.exec"]);

        var decision = evaluator.Evaluate(ProxyWardMode.Audit, tools, "shell.exec");

        Assert.Equal(PolicyDecisionType.WouldBlock, decision.Type);
        Assert.Contains(PolicyReasonCodes.ToolBlocked, decision.Reasons);
    }

    [Fact]
    public void DenyDefaultWithToolNotInAllowListReturnsBlockToolNotAllowedInEnforce()
    {
        var evaluator = new ToolPolicyEvaluator();
        var tools = CreateTools(ToolDefaultMode.Deny, allow: ["repos.search"], block: []);

        var decision = evaluator.Evaluate(ProxyWardMode.Enforce, tools, "issues.list");

        Assert.Equal(PolicyDecisionType.Block, decision.Type);
        Assert.Contains(PolicyReasonCodes.ToolNotAllowed, decision.Reasons);
    }

    [Fact]
    public void DenyDefaultWithToolNotInAllowListInAuditReturnsWouldBlock()
    {
        var evaluator = new ToolPolicyEvaluator();
        var tools = CreateTools(ToolDefaultMode.Deny, allow: [], block: []);

        var decision = evaluator.Evaluate(ProxyWardMode.Audit, tools, "anything");

        Assert.Equal(PolicyDecisionType.WouldBlock, decision.Type);
        Assert.Contains(PolicyReasonCodes.ToolNotAllowed, decision.Reasons);
    }

    [Fact]
    public void DenyDefaultWithToolInAllowListReturnsAllow()
    {
        var evaluator = new ToolPolicyEvaluator();
        var tools = CreateTools(ToolDefaultMode.Deny, allow: ["repos.search"], block: []);

        var decision = evaluator.Evaluate(ProxyWardMode.Enforce, tools, "repos.search");

        Assert.Equal(PolicyDecisionType.Allow, decision.Type);
        Assert.Empty(decision.Reasons);
    }

    [Fact]
    public void AllowDefaultWithUnblockedToolReturnsAllow()
    {
        var evaluator = new ToolPolicyEvaluator();
        var tools = CreateTools(ToolDefaultMode.Allow, allow: [], block: ["shell.exec"]);

        var decision = evaluator.Evaluate(ProxyWardMode.Enforce, tools, "issues.list");

        Assert.Equal(PolicyDecisionType.Allow, decision.Type);
        Assert.Empty(decision.Reasons);
    }

    [Fact]
    public void AllowDefaultWithBlockedToolReturnsBlock()
    {
        var evaluator = new ToolPolicyEvaluator();
        var tools = CreateTools(ToolDefaultMode.Allow, allow: [], block: ["shell.exec"]);

        var decision = evaluator.Evaluate(ProxyWardMode.Enforce, tools, "shell.exec");

        Assert.Equal(PolicyDecisionType.Block, decision.Type);
        Assert.Contains(PolicyReasonCodes.ToolBlocked, decision.Reasons);
    }

    [Fact]
    public void HiddenToolUnderDenyDefaultReturnsBlock()
    {
        var evaluator = new ToolPolicyEvaluator();
        var tools = CreateTools(ToolDefaultMode.Deny, allow: [], block: [], hide: ["repos.search"]);

        var decision = evaluator.Evaluate(ProxyWardMode.Enforce, tools, "repos.search");

        Assert.Equal(PolicyDecisionType.Block, decision.Type);
        Assert.Contains(PolicyReasonCodes.ToolBlocked, decision.Reasons);
    }

    [Fact]
    public void HiddenToolInAuditModeReturnsWouldBlock()
    {
        var evaluator = new ToolPolicyEvaluator();
        var tools = CreateTools(ToolDefaultMode.Allow, allow: [], block: [], hide: ["repos.search"]);

        var decision = evaluator.Evaluate(ProxyWardMode.Audit, tools, "repos.search");

        Assert.Equal(PolicyDecisionType.WouldBlock, decision.Type);
        Assert.Contains(PolicyReasonCodes.ToolBlocked, decision.Reasons);
    }

    [Fact]
    public void HiddenToolWinsOverAllowList()
    {
        var evaluator = new ToolPolicyEvaluator();
        var tools = CreateTools(ToolDefaultMode.Deny, allow: ["repos.search"], block: [], hide: ["repos.search"]);

        var decision = evaluator.Evaluate(ProxyWardMode.Enforce, tools, "repos.search");

        Assert.Equal(PolicyDecisionType.Block, decision.Type);
        Assert.Contains(PolicyReasonCodes.ToolBlocked, decision.Reasons);
    }

    [Fact]
    public void HideDefaultWithUnlistedToolReturnsBlockToolNotAllowed()
    {
        var evaluator = new ToolPolicyEvaluator();
        var tools = CreateTools(ToolDefaultMode.Hide, allow: [], block: []);

        var decision = evaluator.Evaluate(ProxyWardMode.Enforce, tools, "repos.search");

        Assert.Equal(PolicyDecisionType.Block, decision.Type);
        Assert.Contains(PolicyReasonCodes.ToolNotAllowed, decision.Reasons);
    }

    [Fact]
    public void HideDefaultWithAllowedToolReturnsAllow()
    {
        var evaluator = new ToolPolicyEvaluator();
        var tools = CreateTools(ToolDefaultMode.Hide, allow: ["repos.search"], block: []);

        var decision = evaluator.Evaluate(ProxyWardMode.Enforce, tools, "repos.search");

        Assert.Equal(PolicyDecisionType.Allow, decision.Type);
        Assert.Empty(decision.Reasons);
    }

    [Fact]
    public void HideDefaultWithBlockedToolReturnsBlock()
    {
        var evaluator = new ToolPolicyEvaluator();
        var tools = CreateTools(ToolDefaultMode.Hide, allow: [], block: ["repos.delete"]);

        var decision = evaluator.Evaluate(ProxyWardMode.Enforce, tools, "repos.delete");

        Assert.Equal(PolicyDecisionType.Block, decision.Type);
        Assert.Contains(PolicyReasonCodes.ToolBlocked, decision.Reasons);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NullOrBlankToolNameUnderDenyDefaultReturnsBlockToolNotAllowed(string? toolName)
    {
        var evaluator = new ToolPolicyEvaluator();
        var tools = CreateTools(ToolDefaultMode.Deny, allow: ["repos.search"], block: []);

        var decision = evaluator.Evaluate(ProxyWardMode.Enforce, tools, toolName);

        Assert.Equal(PolicyDecisionType.Block, decision.Type);
        Assert.Contains(PolicyReasonCodes.ToolNotAllowed, decision.Reasons);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NullOrBlankToolNameUnderAllowDefaultStillBlocksAsToolNotAllowed(string? toolName)
    {
        var evaluator = new ToolPolicyEvaluator();
        var tools = CreateTools(ToolDefaultMode.Allow, allow: [], block: ["shell.exec"]);

        var decision = evaluator.Evaluate(ProxyWardMode.Enforce, tools, toolName);

        Assert.Equal(PolicyDecisionType.Block, decision.Type);
        Assert.Contains(PolicyReasonCodes.ToolNotAllowed, decision.Reasons);
    }

    [Fact]
    public void AllowDefaultWithToolInAllowListReturnsAllow()
    {
        var evaluator = new ToolPolicyEvaluator();
        var tools = CreateTools(ToolDefaultMode.Allow, allow: ["repos.search"], block: []);

        var decision = evaluator.Evaluate(ProxyWardMode.Enforce, tools, "repos.search");

        Assert.Equal(PolicyDecisionType.Allow, decision.Type);
        Assert.Empty(decision.Reasons);
    }

    [Fact]
    public void ToolNameComparisonIsCaseSensitive()
    {
        var evaluator = new ToolPolicyEvaluator();
        var tools = CreateTools(ToolDefaultMode.Deny, allow: ["repos.search"], block: []);

        var decision = evaluator.Evaluate(ProxyWardMode.Enforce, tools, "Repos.Search");

        Assert.Equal(PolicyDecisionType.Block, decision.Type);
        Assert.Contains(PolicyReasonCodes.ToolNotAllowed, decision.Reasons);
    }

    private static ToolPolicy CreateTools(
        ToolDefaultMode @default,
        IReadOnlyCollection<string> allow,
        IReadOnlyCollection<string> block,
        IReadOnlyCollection<string>? hide = null) =>
        new(@default, allow, block, hide ?? []);
}
