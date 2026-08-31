using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UiPath.Engineering.Mcp.Core;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.CodeAnalysis;
using UiPath.Engineering.Mcp.Core.CodeSearch;
using UiPath.Engineering.Mcp.Core.Configuration;
using UiPath.Engineering.Mcp.Core.Docs;
using UiPath.Engineering.Mcp.Core.Authoring;
using UiPath.Engineering.Mcp.Core.Parsing;
using UiPath.Engineering.Mcp.Core.Planning;
using UiPath.Engineering.Mcp.Providers.Filesystem;
using UiPath.Engineering.Mcp.Providers.Git;
using UiPath.Engineering.Mcp.Providers.GitLab;
using UiPath.Engineering.Mcp.Providers.Skills;
using UiPath.Engineering.Mcp.Providers.UiPathCli;
using UiPath.Engineering.Mcp.Tools;

namespace UiPath.Engineering.Mcp.Server;

public static class McpServiceCollectionExtensions {
    public static IServiceCollection AddUiPathEngineeringServices(
        this IServiceCollection services,
        IConfiguration configuration,
        bool validateHttpAuthOnStart = false) {
        services.Configure<McpServerOptions>(configuration.GetSection("McpServer"));
        if (validateHttpAuthOnStart) {
            services.AddSingleton<IValidateOptions<McpServerOptions>, McpHttpAuthOptionsValidator>();
            services.AddOptions<McpServerOptions>().ValidateOnStart();
        }

        services.Configure<ProjectRootOptions>(configuration.GetSection("Projects"));
        services.Configure<UiPathCliOptions>(configuration.GetSection("UiPathCli"));
        services.Configure<SkillsOptions>(configuration.GetSection("Skills"));
        services.Configure<GitLabOptions>(configuration.GetSection("GitLab"));

        services.AddSingleton<IPathPolicy>(sp =>
            new PathPolicy(sp.GetRequiredService<IOptions<ProjectRootOptions>>().Value));
        services.AddSingleton<IFilesystemProvider, FilesystemProvider>();
        services.AddSingleton<IUiPathCliProvider, UiPathCliProvider>();
        services.AddSingleton<IActivityDiscovery, CliActivityDiscovery>();
        services.AddSingleton<IActivityCatalogResolver, ActivityCatalogResolver>();
        services.AddSingleton<ISkillsProvider, SkillsProvider>();
        services.AddSingleton(sp =>
            new CliCommandPolicy(sp.GetRequiredService<IOptions<UiPathCliOptions>>().Value));
        services.AddSingleton<IGitProvider, GitProvider>();
        services.AddHttpClient<IGitLabProvider, GitLabProvider>();

        services.AddSingleton<ProjectModelBuilder>();
        services.AddSingleton<IProjectModelBuilder>(sp =>
            new CachingProjectModelBuilder(
                sp.GetRequiredService<ProjectModelBuilder>(),
                sp.GetRequiredService<IFilesystemProvider>(),
                sp.GetService<ILogger<CachingProjectModelBuilder>>()));

        services.AddSingleton<ImplementationPlanStore>();
        services.AddSingleton<ProjectKnowledgeStore>();
        services.AddSingleton<ProjectAdrStore>();
        services.AddSingleton<ProjectContextRenderer>();
        services.AddSingleton<ProjectDocsSearch>();
        services.AddSingleton<ProjectDocsValidator>();

        services.AddSingleton<NuGetReferenceResolver>();
        services.AddSingleton<CSharpContextBuilder>();
        services.AddSingleton<ICSharpContextBuilder>(sp =>
            new CSharpAnalysisCache(
                sp.GetRequiredService<CSharpContextBuilder>(),
                sp.GetRequiredService<IFilesystemProvider>(),
                sp.GetRequiredService<NuGetReferenceResolver>(),
                sp.GetService<ILogger<CSharpAnalysisCache>>()));
        services.AddSingleton<ICSharpAnalysisService, CSharpAnalysisService>();
        services.AddSingleton<ICodebaseSearchService, CodebaseSearchService>();

        services.AddHealthChecks()
            .AddCheck<McpReadinessHealthCheck>(
                "ready",
                failureStatus: HealthStatus.Unhealthy,
                tags: [McpHealthEndpoints.ReadyTag]);
        return services;
    }

    public static IMcpServerBuilder AddUiPathMcpServer(this IServiceCollection services, bool restrictToCopilotDefault = true) {
        return services.AddMcpServer()
            .WithToolsFromAssembly(typeof(AnalyzeProjectTool).Assembly)
            .WithResourcesFromAssembly(typeof(AnalyzeProjectTool).Assembly)
            .WithPromptsFromAssembly(typeof(AnalyzeProjectTool).Assembly)
            .WithRequestFilters(filters => {
                filters.AddCallToolFilter(McpToolCallLogging.Filter);
                if (restrictToCopilotDefault) {
                    filters.AddListToolsFilter(next => async (context, cancellationToken) => {
                        var result = await next(context, cancellationToken);
                        if (!CopilotDefaultSurfaceActive(context.Services)) {
                            return result;
                        }

                        CopilotToolSurface.FilterListedTools(result);
                        return result;
                    });
                    filters.AddCallToolFilter(next => async (context, cancellationToken) => {
                        if (CopilotDefaultSurfaceActive(context.Services)
                            && CopilotToolSurface.RejectIfHidden(context.Params?.Name) is { } rejected) {
                            return rejected;
                        }

                        return await next(context, cancellationToken);
                    });
                }

                filters.AddCallToolFilter(next => async (context, cancellationToken) => {
                    var result = await next(context, cancellationToken);
                    if (result.IsError is true) {
                        return result;
                    }

                    if (McpToolErrorMapper.StructuredContentIndicatesError(result.StructuredContent)) {
                        result.IsError = true;
                    }

                    return result;
                });
            });
    }

    private static bool CopilotDefaultSurfaceActive(IServiceProvider? services) {
        if (services is null) {
            return true;
        }

        var surface = services.GetRequiredService<IOptions<McpServerOptions>>().Value.ToolSurface;
        return CopilotConnectorTools.RestrictsSurface(surface);
    }
}
