using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UiPath.Engineering.Mcp.Core.Configuration;
using UiPath.Engineering.Mcp.Server;

var mode = McpHostMode.FromArgs(args);
if (mode == McpHostMode.Kind.Stdio) {
    var stdioBuilder = Host.CreateApplicationBuilder(args);
    stdioBuilder.Logging.AddConsole(options => {
        options.LogToStandardErrorThreshold = LogLevel.Trace;
    });
    stdioBuilder.Services.AddUiPathEngineeringServices(stdioBuilder.Configuration);
    stdioBuilder.Services.AddUiPathMcpServer(restrictToCopilotDefault: false)
        .WithStdioServerTransport();
    await stdioBuilder.Build().RunAsync();
    return;
}

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddUiPathEngineeringServices(builder.Configuration, validateHttpAuthOnStart: true);
builder.Services.Configure<RateLimitOptions>(builder.Configuration.GetSection("McpServer:RateLimit"));

var entra = EntraJwtServiceCollectionExtensions.ReadEntraAuthOptions(builder.Configuration);
builder.Services.AddMcpEntraJwtAuth(entra);

var rateLimit = EntraJwtServiceCollectionExtensions.ReadRateLimitOptions(builder.Configuration);
if (rateLimit.Enabled) {
    builder.Services.AddMcpSseRateLimiter(rateLimit);
}

builder.Services.AddUiPathMcpServer()
    .WithHttpTransport();

var app = builder.Build();
if (rateLimit.Enabled) {
    app.UseRateLimiter();
}

app.UseMiddleware<McpHttpAuthMiddleware>();
McpHealthEndpoints.Map(app);

var sse = app.MapMcp("/sse");
if (rateLimit.Enabled) {
    sse.RequireRateLimiting(McpRateLimitExtensions.SsePolicyName);
}

app.Run();

public partial class Program {
}
