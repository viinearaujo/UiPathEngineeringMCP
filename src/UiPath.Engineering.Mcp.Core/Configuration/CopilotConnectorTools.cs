namespace UiPath.Engineering.Mcp.Core.Configuration;

/// <summary>
/// Canonical Copilot Studio default connector. The HTTP host advertises this
/// unique catalog unless <see cref="McpServerOptions.ToolSurface"/> is All.
/// Hatches, aliases, and GitLab stay registered on the server (LeaveOffNames).
/// </summary>
public static class CopilotConnectorTools {
    public const string SurfaceCopilotDefault = "CopilotDefault";
    public const string SurfaceAll = "All";
    public const int MaxDefaultCount = 31;

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
        "analyze_project_gaps",
        "read_skill",
        "explain_workflow",
        "get_workflow_dependencies",
        "generate_documentation",
        "find_code_symbol",
        "find_code_references",
        "get_code_context",
        "validate_activity_spec",
        "recommend_activities",
        "build_workflow",
        "manage_workflow_data",
        "add_xaml_workflow",
        "create_implementation_plan",
        "create_project",
        "patch_project_json",
        "manage_project_docs",
        "manage_project_file",
        "sync_project_context",
        "validate_project_docs",
    ];

    public static readonly string[] LeaveOffNames = [
        "write_workflow_file",
        "edit_workflow_activity",
        "compile_project",
        "verify_work",
        "run_ui_path_cli",
        "list_skills",
        "search_repository",
        "create_work_items",
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
