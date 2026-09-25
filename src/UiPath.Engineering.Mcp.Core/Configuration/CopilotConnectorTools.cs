namespace UiPath.Engineering.Mcp.Core.Configuration;

/// <summary>
/// Canonical Copilot Studio default connector. The HTTP host advertises this
/// unique catalog unless <see cref="McpServerOptions.ToolSurface"/> is All or ReadOnly.
/// Hatches, aliases, and GitLab stay registered on the server (LeaveOffNames).
/// </summary>
public static class CopilotConnectorTools {
    public const string SurfaceCopilotDefault = "CopilotDefault";
    public const string SurfaceReadOnly = "ReadOnly";
    public const string SurfaceAll = "All";

    /// <summary>Copilot Studio connector cap (merged navigator/knowledge/docs tools).</summary>
    public const int MaxDefaultCount = 25;

    public static readonly string[] DefaultNames = [
        "analyze_project",
        "search_codebase",
        "read_workflow_file",
        "explain_workflow",
        "get_workflow_dependencies",
        "validate_project",
        "analyze_project_gaps",
        "check_work",
        "get_job",
        "navigate_code",
        "create_implementation_plan",
        "get_implementation_plan",
        "update_plan_task",
        "add_coded_workflow",
        "edit_workflow_file",
        "find_activity",
        "insert_activities",
        "validate_activity_spec",
        "build_workflow",
        "manage_workflow_data",
        "search_knowledge",
        "manage_project_content",
        "checkpoint",
        "get_changes",
        "revert_changes",
    ];

    public static readonly string[] LeaveOffNames = [
        "generate_documentation",
        "create_project",
        "add_xaml_workflow",
        "patch_project_json",
        "get_analyzer_rules",
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
        // Moved off the Copilot default connector (still registered on All).
        "get_compile_errors",
        "recommend_activities",
        "manage_packages",
        "get_object_repository",
        "get_activity_metadata",
    ];

    private static readonly HashSet<string> DefaultSet = new(DefaultNames, StringComparer.Ordinal);

    public static bool RestrictsSurface(string? toolSurface) =>
        !string.Equals(toolSurface, SurfaceAll, StringComparison.OrdinalIgnoreCase);

    public static bool IsCopilotDefaultSurface(string? toolSurface) =>
        string.IsNullOrWhiteSpace(toolSurface)
        || string.Equals(toolSurface, SurfaceCopilotDefault, StringComparison.OrdinalIgnoreCase)
        || (!IsReadOnlySurface(toolSurface) && RestrictsSurface(toolSurface));

    public static bool IsReadOnlySurface(string? toolSurface) =>
        string.Equals(toolSurface, SurfaceReadOnly, StringComparison.OrdinalIgnoreCase);

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
