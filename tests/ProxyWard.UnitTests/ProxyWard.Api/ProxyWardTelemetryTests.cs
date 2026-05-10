using System.Diagnostics;
using ProxyWard.Api.Observability;

namespace ProxyWard.UnitTests;

public class ProxyWardTelemetryTests
{
    [Fact]
    public void CreateTagsIncludesStableSafeMetadata()
    {
        var tags = ProxyWardTelemetry.CreateTags(
            new TelemetryMetadata(
                CorrelationId: "corr-1",
                ServerId: "github",
                Method: "tools/call",
                ToolName: "fs.read",
                Mode: "enforce",
                Decision: "block",
                Reasons: ["path_traversal", "dangerous_command"],
                PolicyVersion: "sha256:abc",
                AuditEventType: "tool_call_policy",
                ArgumentSummary: """{"arguments":{"path":"[redacted-path]"}}"""),
            includeCorrelationId: true).ToDictionary(
                pair => pair.Key,
                pair => pair.Value?.ToString(),
                StringComparer.Ordinal);

        Assert.Equal("mcp-proxyward", tags[ProxyWardTelemetry.ServiceNameTag]);
        Assert.Equal("corr-1", tags[ProxyWardTelemetry.CorrelationIdTag]);
        Assert.Equal("github", tags[ProxyWardTelemetry.ServerIdTag]);
        Assert.Equal("tools/call", tags[ProxyWardTelemetry.McpMethodTag]);
        Assert.Equal("fs.read", tags[ProxyWardTelemetry.McpToolNameTag]);
        Assert.Equal("enforce", tags[ProxyWardTelemetry.PolicyModeTag]);
        Assert.Equal("block", tags[ProxyWardTelemetry.PolicyDecisionTag]);
        Assert.Equal("path_traversal,dangerous_command", tags[ProxyWardTelemetry.PolicyReasonsTag]);
        Assert.Equal("sha256:abc", tags[ProxyWardTelemetry.PolicyVersionTag]);
        Assert.Equal("tool_call_policy", tags[ProxyWardTelemetry.AuditEventTypeTag]);
        Assert.Equal(
            """{"arguments":{"path":"[redacted-path]"}}""",
            tags[ProxyWardTelemetry.McpArgumentSummaryTag]);
    }

    [Fact]
    public void CreateMetricTagsCanOmitCorrelationId()
    {
        var tags = ProxyWardTelemetry.CreateTags(
            new TelemetryMetadata(
                CorrelationId: "corr-1",
                ServerId: "github",
                Decision: "allow"),
            includeCorrelationId: false).ToDictionary(
                pair => pair.Key,
                pair => pair.Value?.ToString(),
                StringComparer.Ordinal);

        Assert.False(tags.ContainsKey(ProxyWardTelemetry.CorrelationIdTag));
        Assert.Equal("mcp-proxyward", tags[ProxyWardTelemetry.ServiceNameTag]);
        Assert.Equal("github", tags[ProxyWardTelemetry.ServerIdTag]);
        Assert.Equal("allow", tags[ProxyWardTelemetry.PolicyDecisionTag]);
    }

    [Fact]
    public void CreateTagsCanOmitPolicyVersionAndReasonsForMetrics()
    {
        var tags = ProxyWardTelemetry.CreateTags(
            new TelemetryMetadata(
                CorrelationId: "corr-1",
                ServerId: "github",
                Method: "tools/call",
                Decision: "block",
                Reasons: ["path_outside_allowed_roots", "dangerous_command"],
                PolicyVersion: "sha256:abc",
                ArgumentSummary: """{"arguments":{"path":"[redacted-path]"}}"""),
            includeCorrelationId: false,
            includePolicyVersion: false,
            includeReasons: false,
            includeArgumentSummary: false).ToDictionary(
                pair => pair.Key,
                pair => pair.Value?.ToString(),
                StringComparer.Ordinal);

        Assert.False(tags.ContainsKey(ProxyWardTelemetry.CorrelationIdTag));
        Assert.False(tags.ContainsKey(ProxyWardTelemetry.PolicyVersionTag));
        Assert.False(tags.ContainsKey(ProxyWardTelemetry.PolicyReasonsTag));
        Assert.False(tags.ContainsKey(ProxyWardTelemetry.McpArgumentSummaryTag));
        Assert.Equal("github", tags[ProxyWardTelemetry.ServerIdTag]);
        Assert.Equal("tools/call", tags[ProxyWardTelemetry.McpMethodTag]);
        Assert.Equal("block", tags[ProxyWardTelemetry.PolicyDecisionTag]);
    }

    [Fact]
    public void FormatReasonsReturnsNullForEmptyReasonsAndPreservesOrder()
    {
        Assert.Null(ProxyWardTelemetry.FormatReasons([]));
        Assert.Equal(
            "path_outside_allowed_roots,private_network_target,dangerous_command",
            ProxyWardTelemetry.FormatReasons([
                "path_outside_allowed_roots",
                "private_network_target",
                "dangerous_command"
            ]));
    }

    [Fact]
    public void CreateTagsDoesNotIncludeRawArgumentValues()
    {
        var tags = ProxyWardTelemetry.CreateTags(
            new TelemetryMetadata(
                CorrelationId: "corr-1",
                ServerId: "github",
                Method: "tools/call",
                ToolName: "shell.exec",
                Decision: "block",
                Reasons: ["dangerous_command"]),
            includeCorrelationId: true);

        var combined = string.Join(' ', tags.Select(tag => tag.Value?.ToString()));
        Assert.DoesNotContain("rm -rf", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("/etc/passwd", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("api-token", combined, StringComparison.Ordinal);
    }

    [Fact]
    public void RedactHttpRequestQueryTagsReplacesRawQueryValues()
    {
        using var activity = new Activity("test").Start();
        activity.SetTag(ProxyWardTelemetry.UrlQueryTag, "token=abcDEF123");
        activity.SetTag(ProxyWardTelemetry.HttpTargetTag, "/github/mcp?token=abcDEF123");
        activity.SetTag(ProxyWardTelemetry.HttpUrlTag, "https://proxyward.local/github/mcp?token=abcDEF123");

        ProxyWardTelemetry.RedactHttpRequestQueryTags(
            activity,
            "/github/mcp",
            "?token=abcDEF123",
            "https",
            "proxyward.local");

        var combined = string.Join(' ', activity.TagObjects.Select(tag => tag.Value?.ToString()));
        Assert.DoesNotContain("abcDEF123", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("token=", combined, StringComparison.Ordinal);
        Assert.Contains("[redacted-query]", combined, StringComparison.Ordinal);
        Assert.Contains("[redacted-host]", combined, StringComparison.Ordinal);
    }
}
