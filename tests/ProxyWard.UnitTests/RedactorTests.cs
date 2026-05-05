using System.Text.Json.Nodes;
using ProxyWard.Audit.Redaction;

namespace ProxyWard.UnitTests;

public class RedactorTests
{
    [Fact]
    public void RedactsTokenLikeKeys()
    {
        var redactor = new Redactor();
        var result = redactor.Redact("arguments.token", "abc123secret");

        Assert.True(result.WasRedacted);
        Assert.Equal("[redacted]", result.Value?.GetValue<string>());
    }

    [Fact]
    public void RedactsSecretLikeKeysCaseInsensitively()
    {
        var redactor = new Redactor();
        var result = redactor.Redact("arguments.API_KEY", "k-12345");

        Assert.True(result.WasRedacted);
        Assert.Equal("[redacted]", result.Value?.GetValue<string>());
    }

    [Fact]
    public void RedactsAuthorizationHeaderValue()
    {
        var redactor = new Redactor();
        var result = redactor.Redact("headers.Authorization", "Bearer secret-token");

        Assert.True(result.WasRedacted);
        Assert.Equal("[redacted]", result.Value?.GetValue<string>());
    }

    [Fact]
    public void RedactsUnixPathValues()
    {
        var redactor = new Redactor();
        var result = redactor.Redact("arguments.path", "/etc/passwd");

        Assert.True(result.WasRedacted);
        Assert.Equal("[redacted-path]", result.Value?.GetValue<string>());
    }

    [Fact]
    public void RedactsWindowsDriveLetterPaths()
    {
        var redactor = new Redactor();
        var result = redactor.Redact("arguments.path", @"C:\Users\admin\.ssh\id_rsa");

        Assert.True(result.WasRedacted);
        Assert.Equal("[redacted-path]", result.Value?.GetValue<string>());
    }

    [Fact]
    public void RedactsHomeRelativePaths()
    {
        var redactor = new Redactor();
        var result = redactor.Redact("arguments.target", "~/.ssh/id_rsa");

        Assert.True(result.WasRedacted);
        Assert.Equal("[redacted-path]", result.Value?.GetValue<string>());
    }

    [Fact]
    public void RedactsHostInUrlValues()
    {
        var redactor = new Redactor();
        var result = redactor.Redact("arguments.url", "https://internal-secrets.example.com/v1/keys?id=42");

        Assert.True(result.WasRedacted);
        var redacted = result.Value?.GetValue<string>();
        Assert.NotNull(redacted);
        Assert.Contains("[redacted-host]", redacted);
        Assert.DoesNotContain("internal-secrets.example.com", redacted);
    }

    [Fact]
    public void RedactsCommandLikeKeys()
    {
        var redactor = new Redactor();
        var result = redactor.Redact("arguments.command", "rm -rf /tmp/build");

        Assert.True(result.WasRedacted);
        Assert.Equal("[redacted-command]", result.Value?.GetValue<string>());
    }

    [Fact]
    public void RedactsShellLookingScalarValues()
    {
        var redactor = new Redactor();
        var result = redactor.Redact("arguments.input", "echo ready && echo done");

        Assert.True(result.WasRedacted);
        Assert.Equal("[redacted-command]", result.Value?.GetValue<string>());
    }

    [Fact]
    public void RedactsQueryStringAndUserInfoInUrl()
    {
        var redactor = new Redactor();
        var result = redactor.Redact(
            "arguments.url",
            "https://user:p%40ss@host.example.com/login?token=abcDEF123&id=42");

        Assert.True(result.WasRedacted);
        var redacted = result.Value?.GetValue<string>();
        Assert.NotNull(redacted);
        Assert.DoesNotContain("user", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("p%40ss", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("abcDEF123", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("token=", redacted, StringComparison.Ordinal);
        Assert.Contains("[redacted-host]", redacted);
        Assert.Contains("[redacted-query]", redacted);
    }

    [Fact]
    public void RedactsPathInUrl()
    {
        var redactor = new Redactor();
        var result = redactor.Redact(
            "arguments.url",
            "https://host.example.com/private/repos/secrets?token=abcDEF123");

        Assert.True(result.WasRedacted);
        var redacted = result.Value?.GetValue<string>();
        Assert.NotNull(redacted);
        Assert.DoesNotContain("/private/repos/secrets", redacted, StringComparison.Ordinal);
        Assert.Contains("[redacted-path]", redacted);
    }

    [Theory]
    [InlineData("arguments.host", "internal.example.com")]
    [InlineData("arguments.hosts[0]", "internal.example.com")]
    [InlineData("arguments.targetHost", "10.0.0.5")]
    [InlineData("arguments.targets[0]", "service.internal")]
    [InlineData("arguments.endpoint", "[fd00::1]:8080")]
    [InlineData("arguments.endpoints[0]", "db.internal")]
    [InlineData("arguments.values[0]", "192.168.1.10")]
    public void RedactsHostAndIpValues(string path, string value)
    {
        var redactor = new Redactor();
        var result = redactor.Redact(path, value);

        Assert.True(result.WasRedacted);
        Assert.Equal("[redacted-host]", result.Value?.GetValue<string>());
    }

    [Fact]
    public void DoesNotRedactKeysContainingButNotMatchingSensitiveTokens()
    {
        var redactor = new Redactor();

        var levelResult = redactor.Redact("arguments.authorizationLevel", 5);
        Assert.False(levelResult.WasRedacted);
        Assert.Equal(5, levelResult.Value?.GetValue<int>());

        var countResult = redactor.Redact("arguments.tokenCount", 12);
        Assert.False(countResult.WasRedacted);
        Assert.Equal(12, countResult.Value?.GetValue<int>());
    }

    [Fact]
    public void LeavesPlainStringsUnredacted()
    {
        var redactor = new Redactor();
        var result = redactor.Redact("arguments.cursor", "next-page-42");

        Assert.False(result.WasRedacted);
        Assert.Equal("next-page-42", result.Value?.GetValue<string>());
    }

    [Fact]
    public void LeavesNumericValuesUnredacted()
    {
        var redactor = new Redactor();
        var result = redactor.Redact("arguments.limit", 50);

        Assert.False(result.WasRedacted);
        Assert.Equal(50, result.Value?.GetValue<int>());
    }

    [Fact]
    public void RecursivelyRedactsObjects()
    {
        var redactor = new Redactor();
        var input = new JsonObject
        {
            ["path"] = "/etc/passwd",
            ["token"] = "abc",
            ["limit"] = 10
        };

        var result = redactor.Redact("arguments", input);

        Assert.True(result.WasRedacted);
        var summary = Assert.IsType<JsonObject>(result.Value);
        Assert.Equal("[redacted-path]", summary["path"]?.GetValue<string>());
        Assert.Equal("[redacted]", summary["token"]?.GetValue<string>());
        Assert.Equal(10, summary["limit"]?.GetValue<int>());
    }

    [Fact]
    public void RecursivelyRedactsCommandLikeKeys()
    {
        var redactor = new Redactor();
        var input = new JsonObject
        {
            ["command"] = "powershell.exe -NoProfile -Command Get-ChildItem",
            ["limit"] = 10
        };

        var result = redactor.Redact("arguments", input);

        Assert.True(result.WasRedacted);
        var summary = Assert.IsType<JsonObject>(result.Value);
        Assert.Equal("[redacted-command]", summary["command"]?.GetValue<string>());
        Assert.Equal(10, summary["limit"]?.GetValue<int>());
    }

    [Fact]
    public void RecursivelyRedactsArrays()
    {
        var redactor = new Redactor();
        var input = new JsonArray
        {
            "/etc/secrets",
            "https://leaky.example.com/x",
            42
        };

        var result = redactor.Redact("arguments.values", input);

        Assert.True(result.WasRedacted);
        var summary = Assert.IsType<JsonArray>(result.Value);
        Assert.Equal("[redacted-path]", summary[0]?.GetValue<string>());
        Assert.Contains("[redacted-host]", summary[1]?.GetValue<string>());
        Assert.Equal(42, summary[2]?.GetValue<int>());
    }

    [Fact]
    public void RedactingNullReturnsNullValueAndNotRedacted()
    {
        var redactor = new Redactor();
        var result = redactor.Redact("arguments.maybe", value: null);

        Assert.False(result.WasRedacted);
        Assert.Null(result.Value);
    }
}
