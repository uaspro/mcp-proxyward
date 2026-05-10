using ProxyWard.Management.Api.Configuration;
using ProxyWard.Management.Api.Endpoints;

namespace ProxyWard.Management.Api;

public partial class Program
{
    public static Task Main(string[] args) =>
        CreateApp(args).RunAsync();

    public static WebApplication CreateApp(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        var options = ManagementApiOptionsLoader.LoadOptions(builder.Configuration);
        var auditReadOptions = ManagementApiOptionsLoader.LoadAuditReadOptions(builder.Configuration);

        builder.Services.AddProxyWardManagementApi(options, auditReadOptions);

        var app = builder.Build();

        app.UseCors(ManagementApiServiceCollectionExtensions.CorsPolicyName);
        app.MapProxyWardManagementApi();

        return app;
    }
}
