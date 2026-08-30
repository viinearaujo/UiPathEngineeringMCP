namespace UiPath.Engineering.Mcp.Core.Docs;

public static class ProjectDocsPaths {
    public const string KnowledgeDirectory = "docs/knowledge";
    public const string AdrDirectory = "docs/adr";
    public const string IndexFileName = "index.json";
    public const string AgentsFileName = "AGENTS.md";
    public const string ProjectContextRelativePath = ".claude/rules/project-context.md";
    public const string PlanJsonRelativePath = "docs/implementation-plan.json";
    public const string PlanMarkdownRelativePath = "docs/implementation-plan.md";

    public static string KnowledgeDir(string projectPath) => Combine(projectPath, KnowledgeDirectory);
    public static string KnowledgeIndex(string projectPath) => Combine(projectPath, KnowledgeDirectory, IndexFileName);
    public static string KnowledgeArticle(string projectPath, string id) => Combine(projectPath, KnowledgeDirectory, id + ".md");

    public static string AdrDir(string projectPath) => Combine(projectPath, AdrDirectory);
    public static string AdrIndex(string projectPath) => Combine(projectPath, AdrDirectory, IndexFileName);
    public static string AdrFile(string projectPath, string fileName) => Combine(projectPath, AdrDirectory, fileName);

    public static string AgentsMd(string projectPath) => Combine(projectPath, AgentsFileName);
    public static string ProjectContext(string projectPath) => Combine(projectPath, ProjectContextRelativePath);

    public static string Combine(string projectPath, params string[] parts) {
        if (string.IsNullOrWhiteSpace(projectPath)) {
            throw new ArgumentException("projectPath is required.", nameof(projectPath));
        }

        var current = Path.GetFullPath(projectPath);
        foreach (var part in parts) {
            foreach (var segment in part.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)) {
                current = Path.Combine(current, segment);
            }
        }

        return current;
    }
}
