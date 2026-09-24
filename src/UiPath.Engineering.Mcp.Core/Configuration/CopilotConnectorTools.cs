namespace UiPath.Engineering.Mcp.Core.Configuration;

/// <summary>
/// Canonical Copilot Studio default connector. The HTTP host advertises this
/// unique catalog unless <see cref="McpServerOptions.ToolSurface"/> is All.
/// Hatches, aliases, and GitLab stay registered on the server (LeaveOffNames).
/// </summary>
public static class CopilotConnectorTools {
    public const string SurfaceCopilotDefault = "CopilotDefault";
    public const string SurfaceAll = "All";

    /// <summary>
    /// Copilot Studio connector cap. Bumped from 31 to 34 when the read-only CLI surface
    /// (get_analyzer_rules, manage_packages, get_object_repository) joined the default connector,
    /// and from 34 to 37 when the read-only metadata/knowledge surface joined it
    /// (get_activity_metadata, search_activity_docs, search_uipath_knowledge).
    /// </summary>
    public const int MaxDefaultCount = 37;

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
        "get_analyzer_rules",
        "manage_packages",
        "get_object_repository",
        "get_activity_metadata",
        "search_activity_docs",
        "search_uipath_knowledge",
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
        // Execution-capable: both run arbitrary automation and are additionally gated by
        // UiPathCli:EnableExecution (fail closed, off by default).
        "run_workflow",
        "control_debug_session",
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
