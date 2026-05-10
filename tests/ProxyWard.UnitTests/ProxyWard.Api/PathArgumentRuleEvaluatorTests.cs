using System.Text.Json.Nodes;
using ProxyWard.Core.Policies;
using ProxyWard.Policy.Configuration;
using ProxyWard.Policy.Engine;

namespace ProxyWard.UnitTests;

public class PathArgumentRuleEvaluatorTests
{
    [Fact]
    public void TraversalSegmentInArgumentsBlocksWithPathTraversalInEnforce()
    {
        var evaluator = new PathArgumentRuleEvaluator();
        var paths = CreatePaths(allowedRoots: [], blockTraversal: true);
        var toolCallParams = JsonNode.Parse("""{"name":"fs.read","arguments":{"path":"../etc/passwd"}}""");

        var decision = evaluator.Evaluate(ProxyWardMode.Enforce, paths, toolCallParams);

        Assert.Equal(PolicyDecisionType.Block, decision.Type);
        Assert.Contains(PolicyReasonCodes.PathTraversal, decision.Reasons);
    }

    [Fact]
    public void TraversalSegmentInAuditModeReturnsWouldBlock()
    {
        var evaluator = new PathArgumentRuleEvaluator();
        var paths = CreatePaths(allowedRoots: [], blockTraversal: true);
        var toolCallParams = JsonNode.Parse("""{"name":"fs.read","arguments":{"path":"../etc/passwd"}}""");

        var decision = evaluator.Evaluate(ProxyWardMode.Audit, paths, toolCallParams);

        Assert.Equal(PolicyDecisionType.WouldBlock, decision.Type);
        Assert.Contains(PolicyReasonCodes.PathTraversal, decision.Reasons);
    }

    [Fact]
    public void TraversalSegmentNestedInArrayAndObjectIsDetected()
    {
        var evaluator = new PathArgumentRuleEvaluator();
        var paths = CreatePaths(allowedRoots: [], blockTraversal: true);
        var toolCallParams = JsonNode.Parse(
            """{"name":"fs.batch","arguments":{"files":[{"source":"subdir/../../escape"}]}}""");

        var decision = evaluator.Evaluate(ProxyWardMode.Enforce, paths, toolCallParams);

        Assert.Equal(PolicyDecisionType.Block, decision.Type);
        Assert.Contains(PolicyReasonCodes.PathTraversal, decision.Reasons);
    }

    [Fact]
    public void TraversalSegmentWithBlockTraversalDisabledReturnsAllow()
    {
        var evaluator = new PathArgumentRuleEvaluator();
        var paths = CreatePaths(allowedRoots: [], blockTraversal: false);
        var toolCallParams = JsonNode.Parse("""{"name":"fs.read","arguments":{"path":"../etc/passwd"}}""");

        var decision = evaluator.Evaluate(ProxyWardMode.Enforce, paths, toolCallParams);

        Assert.Equal(PolicyDecisionType.Allow, decision.Type);
        Assert.Empty(decision.Reasons);
    }

    [Fact]
    public void PathOutsideAllowedRootBlocksWithPathOutsideAllowedRoots()
    {
        var evaluator = new PathArgumentRuleEvaluator();
        var paths = CreatePaths(allowedRoots: ["/workspace"], blockTraversal: false);
        var toolCallParams = JsonNode.Parse("""{"name":"fs.read","arguments":{"path":"/etc/passwd"}}""");

        var decision = evaluator.Evaluate(ProxyWardMode.Enforce, paths, toolCallParams);

        Assert.Equal(PolicyDecisionType.Block, decision.Type);
        Assert.Contains(PolicyReasonCodes.PathOutsideAllowedRoots, decision.Reasons);
    }

    [Fact]
    public void TraversalAndOutsideRootProduceBothReasonsInStableOrder()
    {
        var evaluator = new PathArgumentRuleEvaluator();
        var paths = CreatePaths(allowedRoots: ["/workspace"], blockTraversal: true);
        var toolCallParams = JsonNode.Parse(
            """{"name":"fs.read","arguments":{"path":"/workspace/../etc/passwd"}}""");

        var decision = evaluator.Evaluate(ProxyWardMode.Enforce, paths, toolCallParams);

        Assert.Equal(PolicyDecisionType.Block, decision.Type);
        Assert.Equal(
            [PolicyReasonCodes.PathTraversal, PolicyReasonCodes.PathOutsideAllowedRoots],
            decision.Reasons);
    }

    [Fact]
    public void OutsideRootWithBlockTraversalDisabledOnlyReportsOutsideRoot()
    {
        var evaluator = new PathArgumentRuleEvaluator();
        var paths = CreatePaths(allowedRoots: ["/workspace"], blockTraversal: false);
        var toolCallParams = JsonNode.Parse(
            """{"name":"fs.read","arguments":{"path":"/workspace/../etc/passwd"}}""");

        var decision = evaluator.Evaluate(ProxyWardMode.Enforce, paths, toolCallParams);

        Assert.Equal(PolicyDecisionType.Block, decision.Type);
        Assert.Equal([PolicyReasonCodes.PathOutsideAllowedRoots], decision.Reasons);
    }

    [Fact]
    public void PathInsideAllowedRootReturnsAllow()
    {
        var evaluator = new PathArgumentRuleEvaluator();
        var paths = CreatePaths(allowedRoots: ["/workspace"], blockTraversal: false);
        var toolCallParams = JsonNode.Parse(
            """{"name":"fs.read","arguments":{"path":"/workspace/src/Program.cs"}}""");

        var decision = evaluator.Evaluate(ProxyWardMode.Enforce, paths, toolCallParams);

        Assert.Equal(PolicyDecisionType.Allow, decision.Type);
        Assert.Empty(decision.Reasons);
    }

    [Fact]
    public void DotSegmentInsideAllowedRootIsAllowedAndNotTraversal()
    {
        var evaluator = new PathArgumentRuleEvaluator();
        var paths = CreatePaths(allowedRoots: ["/workspace"], blockTraversal: true);
        var toolCallParams = JsonNode.Parse(
            """{"name":"fs.read","arguments":{"path":"/workspace/sub/./file.txt"}}""");

        var decision = evaluator.Evaluate(ProxyWardMode.Enforce, paths, toolCallParams);

        Assert.Equal(PolicyDecisionType.Allow, decision.Type);
        Assert.Empty(decision.Reasons);
    }

    [Fact]
    public void EmptyAllowedRootsAndBlockTraversalDisabledIsNoOp()
    {
        var evaluator = new PathArgumentRuleEvaluator();
        var paths = CreatePaths(allowedRoots: [], blockTraversal: false);
        var toolCallParams = JsonNode.Parse(
            """{"name":"fs.read","arguments":{"path":"/etc/passwd","other":"../escape"}}""");

        var decision = evaluator.Evaluate(ProxyWardMode.Enforce, paths, toolCallParams);

        Assert.Equal(PolicyDecisionType.Allow, decision.Type);
        Assert.Empty(decision.Reasons);
    }

    [Fact]
    public void PathInsideOneOfMultipleAllowedRootsIsAllowed()
    {
        var evaluator = new PathArgumentRuleEvaluator();
        var paths = CreatePaths(allowedRoots: ["/workspace", "/data"], blockTraversal: false);
        var toolCallParams = JsonNode.Parse(
            """{"name":"fs.read","arguments":{"path":"/data/inputs/sample.csv"}}""");

        var decision = evaluator.Evaluate(ProxyWardMode.Enforce, paths, toolCallParams);

        Assert.Equal(PolicyDecisionType.Allow, decision.Type);
        Assert.Empty(decision.Reasons);
    }

    [Fact]
    public void NullParamsReturnsAllow()
    {
        var evaluator = new PathArgumentRuleEvaluator();
        var paths = CreatePaths(allowedRoots: ["/workspace"], blockTraversal: true);

        var decision = evaluator.Evaluate(ProxyWardMode.Enforce, paths, toolCallParams: null);

        Assert.Equal(PolicyDecisionType.Allow, decision.Type);
        Assert.Empty(decision.Reasons);
    }

    [Fact]
    public void ParamsWithoutArgumentsFallsBackToScanningParams()
    {
        var evaluator = new PathArgumentRuleEvaluator();
        var paths = CreatePaths(allowedRoots: ["/workspace"], blockTraversal: false);
        var toolCallParams = JsonNode.Parse("""{"target":"/etc/passwd"}""");

        var decision = evaluator.Evaluate(ProxyWardMode.Enforce, paths, toolCallParams);

        Assert.Equal(PolicyDecisionType.Block, decision.Type);
        Assert.Contains(PolicyReasonCodes.PathOutsideAllowedRoots, decision.Reasons);
    }

    [Fact]
    public void NonStringScalarLeavesAreIgnored()
    {
        var evaluator = new PathArgumentRuleEvaluator();
        var paths = CreatePaths(allowedRoots: ["/workspace"], blockTraversal: true);
        var toolCallParams = JsonNode.Parse(
            """{"name":"fs.read","arguments":{"size":42,"recursive":true,"limit":null}}""");

        var decision = evaluator.Evaluate(ProxyWardMode.Enforce, paths, toolCallParams);

        Assert.Equal(PolicyDecisionType.Allow, decision.Type);
        Assert.Empty(decision.Reasons);
    }

    [Theory]
    [InlineData("repos.search")]
    [InlineData("hello world")]
    [InlineData("issues.list")]
    public void StringsWithoutPathMarkersAreIgnored(string value)
    {
        var evaluator = new PathArgumentRuleEvaluator();
        var paths = CreatePaths(allowedRoots: ["/workspace"], blockTraversal: true);
        var toolCallParams = new JsonObject
        {
            ["arguments"] = new JsonObject { ["q"] = value }
        };

        var decision = evaluator.Evaluate(ProxyWardMode.Enforce, paths, toolCallParams);

        Assert.Equal(PolicyDecisionType.Allow, decision.Type);
        Assert.Empty(decision.Reasons);
    }

    [Theory]
    [InlineData("https://api.github.com/repos/foo/bar")]
    [InlineData("http://example.org/path")]
    [InlineData("file:///workspace/file.txt")]
    public void AbsoluteUrlStringsAreIgnoredByPathRules(string value)
    {
        var evaluator = new PathArgumentRuleEvaluator();
        var paths = CreatePaths(allowedRoots: ["/workspace"], blockTraversal: true);
        var toolCallParams = new JsonObject
        {
            ["arguments"] = new JsonObject { ["url"] = value }
        };

        var decision = evaluator.Evaluate(ProxyWardMode.Enforce, paths, toolCallParams);

        Assert.Equal(PolicyDecisionType.Allow, decision.Type);
        Assert.Empty(decision.Reasons);
    }

    [Fact]
    public void WindowsBackslashPathWithTraversalSegmentIsDetected()
    {
        var evaluator = new PathArgumentRuleEvaluator();
        var paths = CreatePaths(allowedRoots: [], blockTraversal: true);
        var toolCallParams = JsonNode.Parse(
            """{"arguments":{"path":"C:\\Users\\..\\Windows"}}""");

        var decision = evaluator.Evaluate(ProxyWardMode.Enforce, paths, toolCallParams);

        Assert.Equal(PolicyDecisionType.Block, decision.Type);
        Assert.Contains(PolicyReasonCodes.PathTraversal, decision.Reasons);
    }

    [Fact]
    public void TildePrefixedPathIsTreatedAsCandidate()
    {
        var evaluator = new PathArgumentRuleEvaluator();
        var paths = CreatePaths(allowedRoots: ["/workspace"], blockTraversal: false);
        var toolCallParams = JsonNode.Parse(
            """{"arguments":{"path":"~/.ssh/id_rsa"}}""");

        var decision = evaluator.Evaluate(ProxyWardMode.Enforce, paths, toolCallParams);

        Assert.Equal(PolicyDecisionType.Block, decision.Type);
        Assert.Contains(PolicyReasonCodes.PathOutsideAllowedRoots, decision.Reasons);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyOrWhitespaceStringIsIgnored(string value)
    {
        var evaluator = new PathArgumentRuleEvaluator();
        var paths = CreatePaths(allowedRoots: ["/workspace"], blockTraversal: true);
        var toolCallParams = new JsonObject
        {
            ["arguments"] = new JsonObject { ["path"] = value }
        };

        var decision = evaluator.Evaluate(ProxyWardMode.Enforce, paths, toolCallParams);

        Assert.Equal(PolicyDecisionType.Allow, decision.Type);
        Assert.Empty(decision.Reasons);
    }

    [Fact]
    public void RelativePathIsTreatedAsOutsideConfiguredAbsoluteRoot()
    {
        var evaluator = new PathArgumentRuleEvaluator();
        var paths = CreatePaths(allowedRoots: ["/workspace"], blockTraversal: false);
        var toolCallParams = JsonNode.Parse("""{"arguments":{"path":"src/Program.cs"}}""");

        var decision = evaluator.Evaluate(ProxyWardMode.Enforce, paths, toolCallParams);

        Assert.Equal(PolicyDecisionType.Block, decision.Type);
        Assert.Contains(PolicyReasonCodes.PathOutsideAllowedRoots, decision.Reasons);
    }

    [Fact]
    public void AllowedRootEqualsCandidateExactlyIsAllowed()
    {
        var evaluator = new PathArgumentRuleEvaluator();
        var paths = CreatePaths(allowedRoots: ["/workspace"], blockTraversal: false);
        var toolCallParams = JsonNode.Parse("""{"arguments":{"path":"/workspace"}}""");

        var decision = evaluator.Evaluate(ProxyWardMode.Enforce, paths, toolCallParams);

        Assert.Equal(PolicyDecisionType.Allow, decision.Type);
        Assert.Empty(decision.Reasons);
    }

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("/home/user/file.txt")]
    [InlineData("/")]
    public void RootSlashAllowedRootAcceptsAnyAbsolutePath(string candidate)
    {
        var evaluator = new PathArgumentRuleEvaluator();
        var paths = CreatePaths(allowedRoots: ["/"], blockTraversal: false);
        var toolCallParams = new JsonObject
        {
            ["arguments"] = new JsonObject { ["path"] = candidate }
        };

        var decision = evaluator.Evaluate(ProxyWardMode.Enforce, paths, toolCallParams);

        Assert.Equal(PolicyDecisionType.Allow, decision.Type);
        Assert.Empty(decision.Reasons);
    }

    [Fact]
    public void ExplicitNullArgumentsFallsBackToScanningParams()
    {
        var evaluator = new PathArgumentRuleEvaluator();
        var paths = CreatePaths(allowedRoots: ["/workspace"], blockTraversal: false);
        var toolCallParams = new JsonObject
        {
            ["name"] = "fs.read",
            ["arguments"] = null,
            ["target"] = "/etc/passwd"
        };

        var decision = evaluator.Evaluate(ProxyWardMode.Enforce, paths, toolCallParams);

        Assert.Equal(PolicyDecisionType.Block, decision.Type);
        Assert.Contains(PolicyReasonCodes.PathOutsideAllowedRoots, decision.Reasons);
    }

    [Fact]
    public void DuplicatedViolationsAreDeduplicated()
    {
        var evaluator = new PathArgumentRuleEvaluator();
        var paths = CreatePaths(allowedRoots: ["/workspace"], blockTraversal: true);
        var toolCallParams = JsonNode.Parse(
            """{"arguments":{"a":"../escape1","b":"../escape2","c":"/etc/passwd"}}""");

        var decision = evaluator.Evaluate(ProxyWardMode.Enforce, paths, toolCallParams);

        Assert.Equal(PolicyDecisionType.Block, decision.Type);
        Assert.Equal(
            [PolicyReasonCodes.PathTraversal, PolicyReasonCodes.PathOutsideAllowedRoots],
            decision.Reasons);
    }

    private static PathArgumentPolicy CreatePaths(
        IReadOnlyCollection<string> allowedRoots,
        bool blockTraversal) =>
        new(allowedRoots, blockTraversal);
}
