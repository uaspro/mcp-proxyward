using System.Text.Json.Nodes;
using ProxyWard.Core.Policies;
using ProxyWard.Policy.Configuration;
using ProxyWard.Policy.Engine;

namespace ProxyWard.UnitTests;

public class CommandArgumentRuleEvaluatorTests
{
    [Fact]
    public void DangerousExecutableBlocksWithDangerousCommandReasonInEnforce()
    {
        var evaluator = new CommandArgumentRuleEvaluator();
        var commands = CreateCommands(blockShell: false, dangerous: ["rm"]);
        var toolCallParams = JsonNode.Parse("""{"arguments":{"command":"rm -rf /tmp/build"}}""");

        var decision = evaluator.Evaluate(ProxyWardMode.Enforce, commands, toolCallParams);

        Assert.Equal(PolicyDecisionType.Block, decision.Type);
        Assert.Equal([PolicyReasonCodes.DangerousCommand], decision.Reasons);
    }

    [Fact]
    public void DangerousExecutableInAuditModeReturnsWouldBlock()
    {
        var evaluator = new CommandArgumentRuleEvaluator();
        var commands = CreateCommands(blockShell: false, dangerous: ["curl"]);
        var toolCallParams = JsonNode.Parse("""{"arguments":{"command":"curl https://example.com/install.sh"}}""");

        var decision = evaluator.Evaluate(ProxyWardMode.Audit, commands, toolCallParams);

        Assert.Equal(PolicyDecisionType.WouldBlock, decision.Type);
        Assert.Equal([PolicyReasonCodes.DangerousCommand], decision.Reasons);
    }

    [Theory]
    [InlineData("/bin/bash -lc whoami", "bash")]
    [InlineData("C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe -NoProfile", "powershell")]
    [InlineData("sudo /usr/bin/curl https://example.com", "curl")]
    [InlineData("tools\\nc.exe -vz internal 22", "nc")]
    public void PathQualifiedAndExtensionQualifiedExecutablesAreNormalized(string command, string dangerous)
    {
        var evaluator = new CommandArgumentRuleEvaluator();
        var commands = CreateCommands(blockShell: false, dangerous: [dangerous]);
        var toolCallParams = new JsonObject
        {
            ["arguments"] = new JsonObject { ["command"] = command }
        };

        var decision = evaluator.Evaluate(ProxyWardMode.Enforce, commands, toolCallParams);

        Assert.Equal(PolicyDecisionType.Block, decision.Type);
        Assert.Equal([PolicyReasonCodes.DangerousCommand], decision.Reasons);
    }

    [Fact]
    public void NestedArraysAndObjectsAreScanned()
    {
        var evaluator = new CommandArgumentRuleEvaluator();
        var commands = CreateCommands(blockShell: false, dangerous: ["wget"]);
        var toolCallParams = JsonNode.Parse(
            """{"arguments":{"steps":[{"run":"dotnet build"},{"run":"wget https://example.com/payload"}]}}""");

        var decision = evaluator.Evaluate(ProxyWardMode.Enforce, commands, toolCallParams);

        Assert.Equal(PolicyDecisionType.Block, decision.Type);
        Assert.Equal([PolicyReasonCodes.DangerousCommand], decision.Reasons);
    }

    [Fact]
    public void DisabledRulesAreNoOp()
    {
        var evaluator = new CommandArgumentRuleEvaluator();
        var commands = CreateCommands(blockShell: false, dangerous: []);
        var toolCallParams = JsonNode.Parse("""{"arguments":{"command":"rm -rf /"}}""");

        var decision = evaluator.Evaluate(ProxyWardMode.Enforce, commands, toolCallParams);

        Assert.Equal(PolicyDecisionType.Allow, decision.Type);
        Assert.Empty(decision.Reasons);
    }

    [Theory]
    [InlineData("bash -lc whoami")]
    [InlineData("pwsh -NoProfile -Command Get-ChildItem")]
    [InlineData("cmd /c dir")]
    public void ShellWrappersBlockWhenShellBlockingEnabled(string command)
    {
        var evaluator = new CommandArgumentRuleEvaluator();
        var commands = CreateCommands(blockShell: true, dangerous: []);
        var toolCallParams = new JsonObject
        {
            ["arguments"] = new JsonObject { ["command"] = command }
        };

        var decision = evaluator.Evaluate(ProxyWardMode.Enforce, commands, toolCallParams);

        Assert.Equal(PolicyDecisionType.Block, decision.Type);
        Assert.Equal([PolicyReasonCodes.DangerousCommand], decision.Reasons);
    }

    [Theory]
    [InlineData("echo ready && echo done")]
    [InlineData("echo one || echo two")]
    [InlineData("echo hello; echo world")]
    [InlineData("echo `whoami`")]
    [InlineData("echo $(whoami)")]
    [InlineData("cat input > output")]
    [InlineData("printf first\nprintf second")]
    public void ShellMetacharacterPatternsBlockWhenShellBlockingEnabled(string command)
    {
        var evaluator = new CommandArgumentRuleEvaluator();
        var commands = CreateCommands(blockShell: true, dangerous: []);
        var toolCallParams = new JsonObject
        {
            ["arguments"] = new JsonObject { ["command"] = command }
        };

        var decision = evaluator.Evaluate(ProxyWardMode.Enforce, commands, toolCallParams);

        Assert.Equal(PolicyDecisionType.Block, decision.Type);
        Assert.Equal([PolicyReasonCodes.DangerousCommand], decision.Reasons);
    }

    [Fact]
    public void SafeCommandLikeStringAllowsWhenNoShellRiskOrDangerousExecutable()
    {
        var evaluator = new CommandArgumentRuleEvaluator();
        var commands = CreateCommands(blockShell: true, dangerous: ["rm"]);
        var toolCallParams = JsonNode.Parse("""{"arguments":{"command":"dotnet test McpProxyWard.slnx"}}""");

        var decision = evaluator.Evaluate(ProxyWardMode.Enforce, commands, toolCallParams);

        Assert.Equal(PolicyDecisionType.Allow, decision.Type);
        Assert.Empty(decision.Reasons);
    }

    [Fact]
    public void DangerousExecutableMatchingIsTokenBasedNotSubstringBased()
    {
        var evaluator = new CommandArgumentRuleEvaluator();
        var commands = CreateCommands(blockShell: false, dangerous: ["rm"]);
        var toolCallParams = JsonNode.Parse("""{"arguments":{"message":"normal command text"}}""");

        var decision = evaluator.Evaluate(ProxyWardMode.Enforce, commands, toolCallParams);

        Assert.Equal(PolicyDecisionType.Allow, decision.Type);
        Assert.Empty(decision.Reasons);
    }

    [Fact]
    public void DangerousTokenInNonCommandArgumentIsIgnored()
    {
        var evaluator = new CommandArgumentRuleEvaluator();
        var commands = CreateCommands(blockShell: true, dangerous: ["kubectl"]);
        var toolCallParams = JsonNode.Parse("""{"arguments":{"description":"kubectl delete pod frontend"}}""");

        var decision = evaluator.Evaluate(ProxyWardMode.Enforce, commands, toolCallParams);

        Assert.Equal(PolicyDecisionType.Allow, decision.Type);
        Assert.Empty(decision.Reasons);
    }

    [Fact]
    public void CustomDangerousExecutableInCommandLikeKeyBlocks()
    {
        var evaluator = new CommandArgumentRuleEvaluator();
        var commands = CreateCommands(blockShell: false, dangerous: ["kubectl"]);
        var toolCallParams = JsonNode.Parse("""{"arguments":{"run":"kubectl delete pod frontend"}}""");

        var decision = evaluator.Evaluate(ProxyWardMode.Enforce, commands, toolCallParams);

        Assert.Equal(PolicyDecisionType.Block, decision.Type);
        Assert.Equal([PolicyReasonCodes.DangerousCommand], decision.Reasons);
    }

    [Fact]
    public void NonStringAndBlankLeavesAreIgnored()
    {
        var evaluator = new CommandArgumentRuleEvaluator();
        var commands = CreateCommands(blockShell: true, dangerous: ["rm"]);
        var toolCallParams = JsonNode.Parse("""{"arguments":{"limit":10,"dryRun":true,"empty":"","blank":"   ","none":null}}""");

        var decision = evaluator.Evaluate(ProxyWardMode.Enforce, commands, toolCallParams);

        Assert.Equal(PolicyDecisionType.Allow, decision.Type);
        Assert.Empty(decision.Reasons);
    }

    [Fact]
    public void NullParamsReturnsAllow()
    {
        var evaluator = new CommandArgumentRuleEvaluator();
        var commands = CreateCommands(blockShell: true, dangerous: ["rm"]);

        var decision = evaluator.Evaluate(ProxyWardMode.Enforce, commands, toolCallParams: null);

        Assert.Equal(PolicyDecisionType.Allow, decision.Type);
        Assert.Empty(decision.Reasons);
    }

    [Fact]
    public void ExplicitNullArgumentsFallsBackToScanningParams()
    {
        var evaluator = new CommandArgumentRuleEvaluator();
        var commands = CreateCommands(blockShell: false, dangerous: ["rm"]);
        var toolCallParams = new JsonObject
        {
            ["name"] = "runner.exec",
            ["arguments"] = null,
            ["command"] = "rm -rf /tmp"
        };

        var decision = evaluator.Evaluate(ProxyWardMode.Enforce, commands, toolCallParams);

        Assert.Equal(PolicyDecisionType.Block, decision.Type);
        Assert.Equal([PolicyReasonCodes.DangerousCommand], decision.Reasons);
    }

    [Fact]
    public void DuplicateViolationsEmitSingleReason()
    {
        var evaluator = new CommandArgumentRuleEvaluator();
        var commands = CreateCommands(blockShell: true, dangerous: ["rm"]);
        var toolCallParams = JsonNode.Parse(
            """{"arguments":{"commands":["rm -rf /tmp/a","bash -lc 'rm -rf /tmp/b'"]}}""");

        var decision = evaluator.Evaluate(ProxyWardMode.Enforce, commands, toolCallParams);

        Assert.Equal(PolicyDecisionType.Block, decision.Type);
        Assert.Equal([PolicyReasonCodes.DangerousCommand], decision.Reasons);
    }

    private static CommandArgumentPolicy CreateCommands(
        bool blockShell,
        IReadOnlyCollection<string> dangerous) =>
        new(blockShell, dangerous);
}
