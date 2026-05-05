using System.Text;
using ProxyWard.Core.JsonRpc;
using ProxyWard.Core.Mcp;
using ProxyWard.Core.Policies;

namespace ProxyWard.UnitTests;

public class McpMessageParserTests
{
    [Fact]
    public void ParsesSingleJsonRpcRequestObject()
    {
        var parser = new McpMessageParser();
        var body = Encoding.UTF8.GetBytes("""
            {
              "jsonrpc": "2.0",
              "id": 123,
              "method": "tools/call",
              "params": {
                "name": "repos.search",
                "arguments": {
                  "owner": "octocat"
                }
              }
            }
            """);

        var result = parser.Parse(body, "application/json; charset=utf-8");

        Assert.Equal(JsonRpcParseStatus.Parsed, result.Status);
        Assert.False(result.IsBatch);

        var message = Assert.Single(result.Messages);
        Assert.Equal("2.0", message.JsonRpc);
        Assert.Equal("123", message.Id?.ToJsonString());
        Assert.Equal("tools/call", message.Method);
        Assert.Equal(0, message.BatchIndex);
        Assert.Equal("repos.search", message.Params?["name"]?.GetValue<string>());
        Assert.Equal("octocat", message.Params?["arguments"]?["owner"]?.GetValue<string>());
    }

    [Fact]
    public void ParsesJsonRpcBatchInOriginalOrder()
    {
        var parser = new McpMessageParser();
        var body = Encoding.UTF8.GetBytes("""
            [
              { "jsonrpc": "2.0", "id": 1, "method": "initialize" },
              { "jsonrpc": "2.0", "id": 2, "method": "tools/list" },
              { "jsonrpc": "2.0", "id": 3, "method": "tools/call", "params": { "name": "repos.search" } }
            ]
            """);

        var result = parser.Parse(body, "application/json");

        Assert.Equal(JsonRpcParseStatus.Parsed, result.Status);
        Assert.True(result.IsBatch);
        Assert.Equal(["initialize", "tools/list", "tools/call"], result.Messages.Select(message => message.Method));
        Assert.Equal([0, 1, 2], result.Messages.Select(message => message.BatchIndex));
    }

    [Fact]
    public void MalformedJsonReturnsJsonMalformedReason()
    {
        var parser = new McpMessageParser();
        var body = Encoding.UTF8.GetBytes("""{ "jsonrpc": "2.0", "method": """);

        var result = parser.Parse(body, "application/json");

        Assert.Equal(JsonRpcParseStatus.Malformed, result.Status);
        Assert.Empty(result.Messages);
        Assert.Contains(PolicyReasonCodes.JsonMalformed, result.Reasons);
    }

    [Fact]
    public void EmptyBatchReturnsJsonMalformedReason()
    {
        var parser = new McpMessageParser();
        var body = Encoding.UTF8.GetBytes("[]");

        var result = parser.Parse(body, "application/json");

        Assert.Equal(JsonRpcParseStatus.Malformed, result.Status);
        Assert.Empty(result.Messages);
        Assert.Contains(PolicyReasonCodes.JsonMalformed, result.Reasons);
    }

    [Fact]
    public void UnsupportedContentTypeDoesNotParse()
    {
        var parser = new McpMessageParser();
        var body = Encoding.UTF8.GetBytes("""{ "jsonrpc": "2.0", "method": "tools/list" }""");

        var result = parser.Parse(body, "text/plain");

        Assert.Equal(JsonRpcParseStatus.UnsupportedContentType, result.Status);
        Assert.Empty(result.Messages);
        Assert.DoesNotContain(PolicyReasonCodes.JsonMalformed, result.Reasons);
    }
}
