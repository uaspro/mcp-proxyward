using ProxyWard.Api.Configuration;

var builder = WebApplication.CreateBuilder(args);
await builder.AddProxyWardApiAsync();

var app = builder.Build();
app.UseProxyWardApi();

app.Run();

public partial class Program;
