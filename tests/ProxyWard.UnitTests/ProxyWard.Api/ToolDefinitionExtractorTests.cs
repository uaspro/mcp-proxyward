using System.Text;
using ProxyWard.Locking.Tools;

namespace ProxyWard.UnitTests;

public class ToolDefinitionExtractorTests
{
    [Fact]
    public void ExtractReadsToolDefinitionsFromJsonRpcToolsListResponse()
    {
        var extractor = new ToolDefinitionExtractor();
        var body = Encoding.UTF8.GetBytes("""
            {
              "jsonrpc": "2.0",
              "id": 1,
              "result": {
                "tools": [
                  {
                    "name": "repos.search",
                    "title": "Search repositories",
                    "description": "Find repositories",
                    "inputSchema": { "type": "object", "properties": { "q": { "type": "string" } } },
                    "outputSchema": { "type": "object" }
                  }
                ]
              }
            }
            """);

        var result = extractor.Extract(body);

        Assert.True(result.Success);
        var tool = Assert.Single(result.Tools);
        Assert.Equal("repos.search", tool.Name);
        Assert.Equal("Search repositories", tool.Title);
        Assert.Equal("Find repositories", tool.Description);
        Assert.Equal("object", tool.InputSchema?["type"]?.GetValue<string>());
        Assert.Equal("object", tool.OutputSchema?["type"]?.GetValue<string>());
    }

    [Fact]
    public void ExtractReturnsJsonMalformedReasonForInvalidJson()
    {
        var extractor = new ToolDefinitionExtractor();

        var result = extractor.Extract(Encoding.UTF8.GetBytes("{"));

        Assert.False(result.Success);
        Assert.Contains("json_malformed", result.Reasons);
    }

    [Fact]
    public void ExtractReadsToolDefinitionsFromEventStreamMessageResponse()
    {
        var extractor = new ToolDefinitionExtractor();
        var body = Encoding.UTF8.GetBytes("""
            event: endpoint
            data: /mcp/messages?sessionId=huggingface-co

            event: message
            data: {"jsonrpc":"2.0","id":1,"result":{"tools":[{"name":"hf.tool01","title":"HF Tool","description":"Synthetic tool","inputSchema":{"type":"object"}}]}}

            """);

        var result = extractor.Extract(body, "text/event-stream; charset=utf-8");

        Assert.True(result.Success);
        var tool = Assert.Single(result.Tools);
        Assert.Equal("hf.tool01", tool.Name);
        Assert.Equal("HF Tool", tool.Title);
    }

    [Fact]
    public void ExtractSkipsEmptyContentTypeAwareResponse()
    {
        var extractor = new ToolDefinitionExtractor();

        var result = extractor.Extract(Encoding.UTF8.GetBytes("   "), "application/json");

        Assert.False(result.Success);
        Assert.True(result.Skipped);
        Assert.Equal("empty_response", result.SkipReason);
        Assert.Empty(result.Reasons);
    }

    [Fact]
    public void ExtractSkipsEventStreamWithoutJsonRpcMessage()
    {
        var extractor = new ToolDefinitionExtractor();
        var body = Encoding.UTF8.GetBytes("""
            event: endpoint
            data: /mcp/messages?sessionId=huggingface-co

            """);

        var result = extractor.Extract(body, "text/event-stream");

        Assert.False(result.Success);
        Assert.True(result.Skipped);
        Assert.Equal("event_stream_without_jsonrpc_message", result.SkipReason);
        Assert.Empty(result.Reasons);
    }

}
