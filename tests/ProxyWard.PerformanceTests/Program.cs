using ProxyWard.PerformanceTests;

var options = PerformanceOptions.Parse(args);
Directory.CreateDirectory(options.ArtifactsDirectory);

await using var upstream = await SyntheticUpstreamHost.StartAsync();
await using var cleanYarp = await CleanYarpHost.StartAsync(upstream.BaseAddress);
await using var proxyWard = await WorstCaseProxyWardHost.StartAsync(upstream.BaseAddress, options);

await PreflightProbe.EnsureReadyAsync(upstream, "upstream");
await PreflightProbe.EnsureReadyAsync(cleanYarp, "clean YARP");
await PreflightProbe.EnsureReadyAsync(proxyWard, "ProxyWard worst-case");

Console.WriteLine("MCP ProxyWard performance harness");
Console.WriteLine($"Upstream:      {upstream.BaseAddress}");
Console.WriteLine($"Clean YARP:    {cleanYarp.BaseAddress}/mcp");
Console.WriteLine($"ProxyWard:     {proxyWard.BaseAddress}/mcp");
Console.WriteLine($"Rate:          {options.RatePerSecond} req/s per scenario run");
Console.WriteLine($"Warmup:        {options.Warmup}");
Console.WriteLine($"Duration:      {options.Duration}");
Console.WriteLine($"Artifacts:     {Path.GetFullPath(options.ArtifactsDirectory)}");
Console.WriteLine();

foreach (var workload in PerformanceWorkload.CreateRunList(options))
{
    PerformanceRunner.RunScenario(
        $"clean-yarp-{workload.Slug}",
        cleanYarp.BaseAddress,
        workload,
        options);

    PerformanceRunner.RunScenario(
        $"proxyward-worst-case-{workload.Slug}",
        proxyWard.BaseAddress,
        workload,
        options);
}

Console.WriteLine();
Console.WriteLine("Completed. Compare NBomber reports by scenario name.");
