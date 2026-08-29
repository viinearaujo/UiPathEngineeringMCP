using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UiPath.Engineering.Mcp.Server;

var mode = McpHostMode.FromArgs(args);
if (mode == McpHostMode.Kind.Stdio) {
    var stdioBuilder = Host.CreateApplicationBuilder(args);
    stdioBuilder.Logging.AddConsole(options => {
        options.LogToStandardErrorThreshold = LogLevel.Trace;
    });
    stdioBuilder.Services.AddUiPathEngineeringServices(stdioBuilder.Configuration);
    stdioBuilder.Services.AddUiPathMcpServer()
        .WithStdioServerTransport();
    await stdioBuilder.Build().RunAsync();
    return;
}

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddUiPathEngineeringServices(builder.Configuration);
builder.Services.AddUiPathMcpServer()
    .WithHttpTransport();

var app = builder.Build();
app.MapHealthChecks("/health");
app.MapMcp("/sse");
app.Run();
