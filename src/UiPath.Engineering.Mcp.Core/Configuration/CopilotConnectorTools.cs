namespace UiPath.Engineering.Mcp.Core.Configuration;

/// <summary>
/// Canonical Copilot Studio default connector (≤12 tools). The HTTP host
/// advertises this set unless <see cref="McpServerOptions.ToolSurface"/> is All.
/// GitLab and other leave-off tools stay registered on the server.
/// </summary>
public static class CopilotConnectorTools {
    public const string SurfaceCopilotDefault = "CopilotDefault";
    public const string SurfaceAll = "All";
    public const int MaxDefaultCount = 12;

    public static readonly string[] DefaultNames = [
        "analyze_project",
        "search_codebase",
        "read_workflow_file",
        "validate_project",
        "get_implementation_plan",
        "update_plan_task",
        "add_coded_workflow",
        "edit_workflow_file",
        "find_activity",
        "insert_activities",
        "get_compile_errors",
    ];

    public static readonly string[] LeaveOffNames = [
        "find_code_symbol",
        "find_code_references",
        "get_code_context",
        "compile_project",
        "verify_work",
        "run_ui_path_cli",
        "create_implementation_plan",
        "generate_documentation",
        "write_workflow_file",
        "validate_activity_spec",
        "build_workflow",
        "manage_workflow_data",
        "recommend_activities",
        "search_repository",
        "create_work_items",
        "list_skills",
        "read_skill",
        "explain_workflow",
        "create_project",
        "add_xaml_workflow",
        "get_workflow_dependencies",
        "edit_workflow_activity",
        "manage_project_file",
        "patch_project_json",
        "manage_project_docs",
        "sync_project_context",
        "validate_project_docs",
        "analyze_project_gaps",
    ];

    private static readonly HashSet<string> DefaultSet = new(DefaultNames, StringComparer.Ordinal);

    public static bool RestrictsSurface(string? toolSurface) =>
        !string.Equals(toolSurface, SurfaceAll, StringComparison.OrdinalIgnoreCase);

    public static bool IsDefault(string? name) =>
        name is not null && DefaultSet.Contains(name);

    public static List<string> FilterNames(IEnumerable<string> names) =>
        names.Where(IsDefault).ToList();

    /// <summary>Comma-separated DefaultNames. Use this instead of hand-copied catalogs.</summary>
    public static string JoinDefaultNames() => string.Join(", ", DefaultNames);

    /// <summary>README / markdown form of <see cref="JoinDefaultNames"/> (`name` per entry).</summary>
    public static string JoinDefaultNamesMarkdown() =>
        string.Join(", ", DefaultNames.Select(n => $"`{n}`"));
}
