using System.Text.Json.Nodes;
using ProxyWard.Core.JsonRpc;
using ProxyWard.Core.Mcp;

namespace ProxyWard.UnitTests;

public class McpMethodClassifierTests
{
    [Fact]
    public void ToolsCallClassifiesAsToolCallAndExtractsToolName()
    {
        var classifier = new McpMethodClassifier();
        var message = new JsonRpcMessage(
            "2.0",
            Id: null,
            "tools/call",
            new JsonObject
            {
                ["name"] = "repos.search",
                ["arguments"] = new JsonObject()
            },
            BatchIndex: 0);

        var classification = classifier.Classify(message);

        Assert.Equal(McpMessageKind.ToolCall, classification.Kind);
        Assert.Equal("repos.search", classification.ToolName);
    }

    [Fact]
    public void ToolsListClassifiesAsToolsList()
    {
        var classifier = new McpMethodClassifier();
        var message = new JsonRpcMessage(
            "2.0",
            Id: null,
            "tools/list",
            Params: null,
            BatchIndex: 0);

        var classification = classifier.Classify(message);

        Assert.Equal(McpMessageKind.ToolsList, classification.Kind);
        Assert.Null(classification.ToolName);
    }

    [Fact]
    public void ToolsCallWithoutStringNameKeepsToolNameNull()
    {
        var classifier = new McpMethodClassifier();
        var message = new JsonRpcMessage(
            "2.0",
            Id: null,
            "tools/call",
            new JsonObject
            {
                ["name"] = 123
            },
            BatchIndex: 0);

        var classification = classifier.Classify(message);

        Assert.Equal(McpMessageKind.ToolCall, classification.Kind);
        Assert.Null(classification.ToolName);
    }

    [Fact]
    public void OtherMethodClassifiesAsOther()
    {
        var classifier = new McpMethodClassifier();
        var message = new JsonRpcMessage(
            "2.0",
            Id: null,
            "initialize",
            Params: null,
            BatchIndex: 0);

        var classification = classifier.Classify(message);

        Assert.Equal(McpMessageKind.Other, classification.Kind);
        Assert.Null(classification.ToolName);
    }
}
