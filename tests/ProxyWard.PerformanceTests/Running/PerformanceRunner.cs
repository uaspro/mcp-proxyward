using System.Globalization;
using System.Net.Http.Headers;
using NBomber.Contracts.Stats;
using NBomber.CSharp;

namespace ProxyWard.PerformanceTests;

internal static class PerformanceRunner
{
    private static readonly MediaTypeHeaderValue JsonMediaType = new("application/json");

    public static void RunScenario(
        string scenarioName,
        string baseAddress,
        PerformanceWorkload workload,
        PerformanceOptions options)
    {
        using var http = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = Math.Max(options.RatePerSecond * 4, 64)
        })
        {
            BaseAddress = new Uri(baseAddress, UriKind.Absolute),
            Timeout = TimeSpan.FromSeconds(30)
        };

        var scenario = Scenario.Create(scenarioName, async _ =>
            {
                using var content = new ByteArrayContent(workload.PayloadBytes);
                content.Headers.ContentType = JsonMediaType;

                using var response = await http.PostAsync("/mcp", content).ConfigureAwait(false);
                var statusCode = (int)response.StatusCode;

                return response.IsSuccessStatusCode
                    ? Response.Ok(statusCode: statusCode.ToString(CultureInfo.InvariantCulture))
                    : Response.Fail(statusCode: statusCode.ToString(CultureInfo.InvariantCulture));
            })
            .WithWarmUpDuration(options.Warmup)
            .WithLoadSimulations(
                Simulation.Inject(
                    rate: options.RatePerSecond,
                    interval: TimeSpan.FromSeconds(1),
                    during: options.Duration));

        Console.WriteLine($"Running {scenarioName}...");

        NBomberRunner
            .RegisterScenarios(scenario)
            .WithTestSuite("mcp-proxyward")
            .WithTestName(scenarioName)
            .WithReportFolder(options.ArtifactsDirectory)
            .WithReportFileName(scenarioName)
            .WithReportFormats(ReportFormat.Txt, ReportFormat.Html, ReportFormat.Csv)
            .Run();
    }
}
