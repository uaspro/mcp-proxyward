using System.Text.Json.Nodes;
using ProxyWard.Audit.Redaction;

namespace ProxyWard.UnitTests;

public class SecretPatternSetTests
{
    [Fact]
    public void MatchJsonReportsLiteralMatchTypeWithoutRawValue()
    {
        var patternSet = SecretPatternSet.Create(
            new SecretRedactionOptions(RedactInLogs: true, Patterns: ["ghp_"]));

        var result = patternSet.MatchJson(JsonNode.Parse("""{"text":"token ghp_secret"}"""));

        Assert.True(result.WasMatched);
        Assert.Equal(["literal"], result.MatchTypes);
    }

    [Fact]
    public void MatchJsonReportsRegexMatchTypeWithoutRawValue()
    {
        var patternSet = SecretPatternSet.Create(
            new SecretRedactionOptions(RedactInLogs: true, Patterns: ["/github_pat_[A-Za-z0-9_]+/"]));

        var result = patternSet.MatchJson(JsonNode.Parse("""{"text":"token github_pat_secret_123"}"""));

        Assert.True(result.WasMatched);
        Assert.Equal(["regex"], result.MatchTypes);
    }

    [Fact]
    public void MatchJsonIgnoresConfiguredPatternsWhenRedactionDisabled()
    {
        var patternSet = SecretPatternSet.Create(
            new SecretRedactionOptions(RedactInLogs: false, Patterns: ["ghp_"]));

        var result = patternSet.MatchJson(JsonNode.Parse("""{"text":"token ghp_secret"}"""));

        Assert.False(result.WasMatched);
        Assert.Empty(result.MatchTypes);
    }
}
