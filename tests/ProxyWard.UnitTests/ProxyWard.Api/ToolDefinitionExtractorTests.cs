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
}
