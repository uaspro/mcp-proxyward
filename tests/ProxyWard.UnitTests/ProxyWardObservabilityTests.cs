using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ProxyWard.Api.Observability;
using ProxyWard.Policy.Configuration;

namespace ProxyWard.UnitTests;

public class ProxyWardObservabilityTests
{
    [Fact]
    public void CreateExportPlanEnablesOtlpWhenConfigured()
    {
        var policy = LoadPolicy("""
            observability:
              serviceName: proxyward-test
              otlp:
                enabled: true
                endpoint: http://127.0.0.1:4317
            """);

        var plan = ProxyWardObservability.CreateExportPlan(policy, _ => null);

        Assert.True(plan.OtlpEnabled);
        Assert.Equal("http://127.0.0.1:4317", plan.OtlpEndpoint!.OriginalString);
        Assert.Equal("proxyward-test", plan.ServiceName);
    }

    [Fact]
    public void CreateExportPlanDisablesOtlpWhenConfiguredOff()
    {
        var policy = LoadPolicy("""
            observability:
              serviceName: proxyward-test
              otlp:
                enabled: false
                endpoint: http://127.0.0.1:4317
            """);

        var plan = ProxyWardObservability.CreateExportPlan(policy, _ => null);

        Assert.False(plan.OtlpEnabled);
        Assert.Null(plan.OtlpEndpoint);
    }

    [Fact]
    public void CreateExportPlanEnablesApplicationInsightsWhenConfiguredAndEnvironmentValueExists()
    {
        var policy = LoadPolicy("""
            observability:
              serviceName: proxyward-test
              applicationInsights:
                enabled: true
                connectionStringEnv: APPLICATIONINSIGHTS_CONNECTION_STRING
            """);

        var plan = ProxyWardObservability.CreateExportPlan(
            policy,
            name => name == "APPLICATIONINSIGHTS_CONNECTION_STRING"
                ? "InstrumentationKey=00000000-0000-0000-0000-000000000000;IngestionEndpoint=https://example.com/"
                : null);

        Assert.True(plan.ApplicationInsightsEnabled);
        Assert.Equal("APPLICATIONINSIGHTS_CONNECTION_STRING", plan.ApplicationInsightsConnectionStringEnvironmentVariable);
        Assert.Contains("InstrumentationKey=", plan.ApplicationInsightsConnectionString, StringComparison.Ordinal);
        Assert.False(plan.ApplicationInsightsMissingConnectionString);
    }

    [Fact]
    public void CreateExportPlanSkipsApplicationInsightsWhenDisabled()
    {
        var policy = LoadPolicy("""
            observability:
              serviceName: proxyward-test
              applicationInsights:
                enabled: false
                connectionStringEnv: APPLICATIONINSIGHTS_CONNECTION_STRING
            """);

        var plan = ProxyWardObservability.CreateExportPlan(
            policy,
            _ => "InstrumentationKey=00000000-0000-0000-0000-000000000000;IngestionEndpoint=https://example.com/");

        Assert.False(plan.ApplicationInsightsEnabled);
        Assert.Null(plan.ApplicationInsightsConnectionString);
        Assert.False(plan.ApplicationInsightsMissingConnectionString);
    }

    [Fact]
    public void AddProxyWardObservabilityDoesNotRegisterAzureMonitorWhenApplicationInsightsDisabled()
    {
        var services = new ServiceCollection();
        var logging = new TestLoggingBuilder(services);
        var policy = LoadPolicy("""
            observability:
              serviceName: proxyward-test
              applicationInsights:
                enabled: false
                connectionStringEnv: APPLICATIONINSIGHTS_CONNECTION_STRING
            """);

        var plan = services.AddProxyWardObservability(
            logging,
            policy,
            _ => "InstrumentationKey=00000000-0000-0000-0000-000000000000;IngestionEndpoint=https://example.com/");

        Assert.False(plan.ApplicationInsightsEnabled);
        Assert.DoesNotContain(services, ContainsAzureMonitorType);
    }

    [Fact]
    public void CreateExportPlanSkipsApplicationInsightsWhenEnvironmentValueIsMissing()
    {
        var policy = LoadPolicy("""
            observability:
              serviceName: proxyward-test
              applicationInsights:
                enabled: true
                connectionStringEnv: CUSTOM_AI_CONNECTION_STRING
            """);

        var plan = ProxyWardObservability.CreateExportPlan(policy, _ => null);

        Assert.False(plan.ApplicationInsightsEnabled);
        Assert.Equal("CUSTOM_AI_CONNECTION_STRING", plan.ApplicationInsightsConnectionStringEnvironmentVariable);
        Assert.Null(plan.ApplicationInsightsConnectionString);
        Assert.True(plan.ApplicationInsightsMissingConnectionString);
    }

    [Fact]
    public void CreateExportPlanPreservesSamplingRatio()
    {
        var policy = LoadPolicy("""
            observability:
              serviceName: proxyward-test
              sampling:
                tracesRatio: 0.25
            """);

        var plan = ProxyWardObservability.CreateExportPlan(policy, _ => null);

        Assert.Equal(0.25, plan.TracesRatio);
    }

    private static ProxyWardPolicy LoadPolicy(string observabilityOverride)
    {
        var yaml =
            $$"""
            mode: audit
            inspection:
              maxBodyBytes: 4096
              unsupportedStreaming: warn
              batchToolCalls: failClosed
            audit:
              sink: sqlite
              sqlitePath: ./proxyward.db
            {{observabilityOverride}}
              console:
                enabled: false
            servers:
              github:
                route: /github/mcp
                upstream: http://127.0.0.1:8080/mcp
                allowed: true
                tools:
                  default: allow
                  allow: []
                  block: []
                arguments:
                  paths:
                    allowedRoots: []
                    blockTraversal: false
                  hosts:
                    allow: []
                    blockPrivateNetworks: false
                  commands:
                    blockShell: false
                    dangerous: []
            """;

        return ProxyWardPolicyLoader.Load(yaml);
    }

    private static bool ContainsAzureMonitorType(ServiceDescriptor descriptor) =>
        IsAzureMonitorType(descriptor.ServiceType)
        || IsAzureMonitorType(descriptor.ImplementationType)
        || IsAzureMonitorType(descriptor.ImplementationInstance?.GetType());

    private static bool IsAzureMonitorType(Type? type)
    {
        if (type is null)
        {
            return false;
        }

        if (type.Namespace?.StartsWith("Azure.Monitor.OpenTelemetry", StringComparison.Ordinal) == true)
        {
            return true;
        }

        return type.IsGenericType
            && type.GetGenericArguments().Any(IsAzureMonitorType);
    }

    private sealed class TestLoggingBuilder(IServiceCollection services) : ILoggingBuilder
    {
        public IServiceCollection Services { get; } = services;
    }
}
