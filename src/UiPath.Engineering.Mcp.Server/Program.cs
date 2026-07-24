using ModelContextProtocol.AspNetCore;
using UiPath.Engineering.Mcp.Core.Configuration;
using UiPath.Engineering.Mcp.Providers.Filesystem;
using UiPath.Engineering.Mcp.Providers.UiPathCli;

var builder = WebApplication.CreateBuilder(args);

// Bind configuration
builder.Services.Configure<McpServerOptions>(builder.Configuration.GetSection("McpServer"));
builder.Services.Configure<ProjectRootOptions>(builder.Configuration.GetSection("Projects"));
builder.Services.Configure<UiPathCliOptions>(builder.Configuration.GetSection("UiPathCli"));

// Register providers
builder.Services.AddSingleton<IFilesystemProvider, FilesystemProvider>();
builder.Services.AddSingleton<IUiPathCliProvider, UiPathCliProvider>();

// Add health checks and MCP server
builder.Services.AddHealthChecks();
builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithToolsFromAssembly();

var app = builder.Build();

// Map endpoints
app.MapHealthChecks("/health");
app.MapMcp("/sse");

app.Run();