using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using ProxyWard.Api.Hosts;
using ProxyWard.Api.Middleware;
using ProxyWard.Api.Runtime;
using ProxyWard.Audit.Redaction;
using ProxyWard.Audit.Sinks;
using ProxyWard.Core.JsonRpc;
using ProxyWard.Core.Mcp;
using ProxyWard.Policy.Configuration;
using ProxyWard.Policy.Engine;

namespace ProxyWard.UnitTests;

public class RuntimePolicySnapshotMiddlewareTests
{
    [Fact]
    public async Task ToolPolicyUsesRequestSnapshotWhenProviderChangesMidRequest()
    {
        var capturedPolicy = ProxyWardPolicyLoader.Load(AllowedToolYaml);
        var replacementServer = capturedPolicy.Servers["sample"] with
        {
            Tools = capturedPolicy.Servers["sample"].Tools with { Allow = [] }
        };
        var replacementPolicy = capturedPolicy with
        {
            Mode = ProxyWardMode.Enforce,
            Servers = new SortedDictionary<string, ServerPolicy>(StringComparer.Ordinal)
            {
                ["sample"] = replacementServer
            },
            VersionHash = "replacement"
        };
        var provider = new InMemoryProxyWardPolicyProvider(capturedPolicy);
        provider.Replace(replacementPolicy);

        var nextCalled = false;
        var middleware = new ToolPolicyMiddleware(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            provider,
            new ToolPolicyEvaluator(),
            new PathArgumentRuleEvaluator(),
            new HostArgumentRuleEvaluator(new SystemHostResolver()),
            new CommandArgumentRuleEvaluator(),
            new ArgumentPolicyOverrideResolver(),
            new McpMethodClassifier(),
            new Redactor(),
            new NullAuditSink(),
            NullLogger<ToolPolicyMiddleware>.Instance);

        var context = new DefaultHttpContext();
        context.Items[ServerResolutionItems.ServerPolicy] = capturedPolicy.Servers["sample"];
        context.Items[ServerResolutionItems.PolicySnapshot] = capturedPolicy;
        context.Items[RequestInspectionItems.JsonRpcParseResult] = JsonRpcParseResult.Parsed(
            [
                new JsonRpcMessage(
                    "2.0",
                    JsonValue.Create(1),
                    "tools/call",
                    new JsonObject
                    {
                        ["name"] = "safe.tool",
                        ["arguments"] = new JsonObject()
                    },
                    BatchIndex: 0)
            ],
            isBatch: false);

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
        Assert.Same(replacementPolicy, provider.Current);
    }

    private const string AllowedToolYaml = """
        mode: audit
        inspection:
          maxBodyBytes: 1048576
          unsupportedStreaming: warn
        audit:
          enabled: true
        observability:
          serviceName: mcp-proxyward
          console:
            enabled: true
          otlp:
            enabled: false
            endpoint: http://otel-collector:4317
          applicationInsights:
            enabled: false
            connectionStringEnv: APPLICATIONINSIGHTS_CONNECTION_STRING
          sampling:
            tracesRatio: 1.0
        servers:
          sample:
            route: /sample/mcp
            upstream: http://localhost:8080/mcp
            allowed: true
            tools:
              default: deny
              allow:
                - safe.tool
              block: []
            arguments:
              paths:
                allowedRoots:
                  - /workspace
                blockTraversal: true
              hosts:
                allow: []
                blockPrivateNetworks: true
              commands:
                blockShell: true
                dangerous:
                  - rm
        """;
}
