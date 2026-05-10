using System.Text.Json.Nodes;
using ProxyWard.Locking.Tools;

namespace ProxyWard.UnitTests;

public class ToolFingerprinterTests
{
    [Fact]
    public void FingerprintCompleteToolCreatesSeparateHashes()
    {
        var fingerprinter = new ToolFingerprinter();
        var tool = new DiscoveredTool(
            Name: "repos.search",
            Title: "Search repositories",
            Description: "Find repositories",
            InputSchema: JsonNode.Parse("""{"type":"object","properties":{"q":{"type":"string"}}}"""),
            OutputSchema: JsonNode.Parse("""{"type":"object","properties":{"items":{"type":"array"}}}"""));

        var fingerprint = fingerprinter.Fingerprint(tool);

        AssertHash(fingerprint.NameHash);
        AssertHash(fingerprint.TitleHash);
        AssertHash(fingerprint.DescriptionHash);
        AssertHash(fingerprint.InputSchemaHash);
        AssertHash(fingerprint.OutputSchemaHash);
        Assert.NotEqual(fingerprint.NameHash, fingerprint.TitleHash);
        Assert.NotEqual(fingerprint.InputSchemaHash, fingerprint.OutputSchemaHash);
    }

    [Fact]
    public void SemanticallyEquivalentSchemasHashIdentically()
    {
        var fingerprinter = new ToolFingerprinter();
        var first = new DiscoveredTool(
            Name: "tool",
            Title: null,
            Description: null,
            InputSchema: JsonNode.Parse("""
                {
                  "properties": {
                    "q": { "type": "string" },
                    "limit": { "type": "integer" }
                  },
                  "type": "object",
                  "required": ["q", "limit"]
                }
                """),
            OutputSchema: null);
        var second = first with
        {
            InputSchema = JsonNode.Parse("""{"required":["q","limit"],"type":"object","properties":{"limit":{"type":"integer"},"q":{"type":"string"}}}""")
        };

        var firstHash = fingerprinter.Fingerprint(first).InputSchemaHash;
        var secondHash = fingerprinter.Fingerprint(second).InputSchemaHash;

        Assert.Equal(firstHash, secondHash);
    }

    [Fact]
    public void AbsentFieldsAreDistinctFromEmptyValues()
    {
        var fingerprinter = new ToolFingerprinter();
        var absent = new DiscoveredTool(null, null, null, null, null);
        var empty = new DiscoveredTool(
            Name: string.Empty,
            Title: string.Empty,
            Description: string.Empty,
            InputSchema: new JsonObject(),
            OutputSchema: new JsonObject());

        var absentFingerprint = fingerprinter.Fingerprint(absent);
        var emptyFingerprint = fingerprinter.Fingerprint(empty);

        Assert.Null(absentFingerprint.NameHash);
        Assert.Null(absentFingerprint.TitleHash);
        Assert.Null(absentFingerprint.DescriptionHash);
        Assert.Null(absentFingerprint.InputSchemaHash);
        Assert.Null(absentFingerprint.OutputSchemaHash);
        AssertHash(emptyFingerprint.NameHash);
        AssertHash(emptyFingerprint.TitleHash);
        AssertHash(emptyFingerprint.DescriptionHash);
        AssertHash(emptyFingerprint.InputSchemaHash);
        AssertHash(emptyFingerprint.OutputSchemaHash);
    }

    [Fact]
    public void DescriptionHashNormalizesLineEndingsOnly()
    {
        var fingerprinter = new ToolFingerprinter();
        var windows = new DiscoveredTool("tool", null, "line 1\r\nline 2", null, null);
        var unix = windows with { Description = "line 1\nline 2" };
        var trimmed = windows with { Description = "line 1\nline 2 " };

        var windowsHash = fingerprinter.Fingerprint(windows).DescriptionHash;
        var unixHash = fingerprinter.Fingerprint(unix).DescriptionHash;
        var trimmedHash = fingerprinter.Fingerprint(trimmed).DescriptionHash;

        Assert.Equal(windowsHash, unixHash);
        Assert.NotEqual(windowsHash, trimmedHash);
    }

    private static void AssertHash(string? hash)
    {
        Assert.NotNull(hash);
        Assert.StartsWith("sha256:", hash, StringComparison.Ordinal);
        Assert.Equal("sha256:".Length + 64, hash.Length);
    }
}
