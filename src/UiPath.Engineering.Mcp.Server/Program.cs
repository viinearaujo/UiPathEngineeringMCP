using Microsoft.Extensions.Options;
using ModelContextProtocol.AspNetCore;
using UiPath.Engineering.Mcp.Core.Configuration;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.CodeAnalysis;
using UiPath.Engineering.Mcp.Core.Parsing;
using UiPath.Engineering.Mcp.Core.Planning;
using UiPath.Engineering.Mcp.Providers.Filesystem;
using UiPath.Engineering.Mcp.Providers.Git;
using UiPath.Engineering.Mcp.Providers.GitLab;
using UiPath.Engineering.Mcp.Providers.Skills;
using UiPath.Engineering.Mcp.Providers.UiPathCli;
using UiPath.Engineering.Mcp.Tools;

var builder = WebApplication.CreateBuilder(args);

// Bind configuration
builder.Services.Configure<McpServerOptions>(builder.Configuration.GetSection("McpServer"));
builder.Services.Configure<ProjectRootOptions>(builder.Configuration.GetSection("Projects"));
builder.Services.Configure<UiPathCliOptions>(builder.Configuration.GetSection("UiPathCli"));
builder.Services.Configure<SkillsOptions>(builder.Configuration.GetSection("Skills"));
builder.Services.Configure<GitLabOptions>(builder.Configuration.GetSection("GitLab"));

// Register providers
builder.Services.AddSingleton<IFilesystemProvider, FilesystemProvider>();
builder.Services.AddSingleton<IUiPathCliProvider, UiPathCliProvider>();
builder.Services.AddSingleton<ISkillsProvider, SkillsProvider>();
builder.Services.AddSingleton(sp =>
    new CliCommandPolicy(sp.GetRequiredService<IOptions<UiPathCliOptions>>().Value));
builder.Services.AddSingleton<IGitProvider, GitProvider>();
builder.Services.AddHttpClient<IGitLabProvider, GitLabProvider>();

// Register parsing services
builder.Services.AddSingleton<ProjectModelBuilder>();
builder.Services.AddSingleton<IProjectModelBuilder>(sp =>
    new CachingProjectModelBuilder(
        sp.GetRequiredService<ProjectModelBuilder>(),
        sp.GetRequiredService<IFilesystemProvider>()));

// Implementation-plan persistence (docs/implementation-plan.json inside each project).
builder.Services.AddSingleton<ImplementationPlanStore>();

// C# semantic analysis (Roslyn). The context builder is wrapped in the
// fingerprint cache so compilations are only rebuilt when project files change.
builder.Services.AddSingleton<NuGetReferenceResolver>();
builder.Services.AddSingleton<CSharpContextBuilder>();
builder.Services.AddSingleton<ICSharpContextBuilder>(sp =>
    new CSharpAnalysisCache(
        sp.GetRequiredService<CSharpContextBuilder>(),
        sp.GetRequiredService<IFilesystemProvider>()));
builder.Services.AddSingleton<ICSharpAnalysisService, CSharpAnalysisService>();

// Add health checks and MCP server.
// IMPORTANT: the tool classes live in the UiPath.Engineering.Mcp.Tools assembly,
// NOT in this Server (entry) assembly. WithToolsFromAssembly() with no argument
// scans the entry assembly and would therefore discover ZERO tools. We must point
// the scan at the Tools assembly explicitly.
builder.Services.AddHealthChecks();
builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithToolsFromAssembly(typeof(AnalyzeProjectTool).Assembly);

var app = builder.Build();

// Map endpoints
app.MapHealthChecks("/health");

// MapMcp wires the Streamable HTTP MCP endpoint at the given path.
// The path "/sse" is kept to match the existing Copilot registration docs; note
// that this is the modern Streamable HTTP transport (it handles GET/POST/DELETE
// on this single path), not the deprecated legacy SSE transport.
app.MapMcp("/sse");

app.Run();
