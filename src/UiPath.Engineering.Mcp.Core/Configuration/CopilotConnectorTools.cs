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
        "find_activity",
        "validate_activity_spec",
        "build_workflow",
        "insert_activities",
        "manage_workflow_data",
        "validate_project",
        "get_implementation_plan",
        "update_plan_task",
        "recommend_activities",
    ];

    public static readonly string[] LeaveOffNames = [
        "find_code_symbol",
        "find_code_references",
        "get_code_context",
        "get_compile_errors",
        "compile_project",
        "verify_work",
        "run_ui_path_cli",
        "create_implementation_plan",
        "generate_documentation",
        "write_workflow_file",
        "search_repository",
        "create_work_items",
        "list_skills",
        "read_skill",
    ];

    private static readonly HashSet<string> DefaultSet = new(DefaultNames, StringComparer.Ordinal);

    public static bool RestrictsSurface(string? toolSurface) =>
        !string.Equals(toolSurface, SurfaceAll, StringComparison.OrdinalIgnoreCase);

    public static bool IsDefault(string? name) =>
        name is not null && DefaultSet.Contains(name);

    public static List<string> FilterNames(IEnumerable<string> names) =>
        names.Where(IsDefault).ToList();
}
