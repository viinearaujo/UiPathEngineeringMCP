using System.Text;
using System.Text.RegularExpressions;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Core.Docs;

public sealed class ProjectContextRenderer {
    public const string StartMarker = "<!-- uipath-project-context:start -->";
    public const string EndMarker = "<!-- uipath-project-context:end -->";
    public const int MaxLines = 200;

    private static readonly Regex MetadataPattern = new(
        @"<!--\s*discovery-metadata:\s*cs=(?<cs>\d+)\s+xaml=(?<xaml>\d+)\s+deps=(?<deps>\d+)\s*-->",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly IFilesystemProvider _filesystem;

    public ProjectContextRenderer(IFilesystemProvider filesystem) => _filesystem = filesystem;

    public static string MetadataComment(int csharpCount, int xamlCount, int dependencyCount) =>
        $"<!-- discovery-metadata: cs={csharpCount} xaml={xamlCount} deps={dependencyCount} -->";

    public static bool TryParseMetadata(string content, out int csharpCount, out int xamlCount, out int dependencyCount) {
        csharpCount = 0;
        xamlCount = 0;
        dependencyCount = 0;
        var match = MetadataPattern.Match(content);
        if (!match.Success) {
            return false;
        }

        csharpCount = int.Parse(match.Groups["cs"].Value);
        xamlCount = int.Parse(match.Groups["xaml"].Value);
        dependencyCount = int.Parse(match.Groups["deps"].Value);
        return true;
    }

    public static bool HasMarkers(string? content) =>
        content is not null
        && content.Contains(StartMarker, StringComparison.Ordinal)
        && content.Contains(EndMarker, StringComparison.Ordinal);

    public static (int CSharp, int Xaml, int Dependencies) Counts(UiPathProjectModel model) =>
        (model.CodedWorkflows.Count, model.Workflows.Count, model.Packages.Count);

    public string RenderMarkdown(UiPathProjectModel model) {
        var (cs, xaml, deps) = Counts(model);
        var sb = new StringBuilder();
        sb.AppendLine($"# {model.ProjectName}");
        sb.AppendLine();
        sb.AppendLine(MetadataComment(cs, xaml, deps));
        sb.AppendLine();
        sb.AppendLine($"- Path: `{model.ProjectPath}`");
        if (!string.IsNullOrWhiteSpace(model.Description)) {
            sb.AppendLine($"- Description: {model.Description}");
        }
        if (!string.IsNullOrWhiteSpace(model.TargetFramework)) {
            sb.AppendLine($"- Target framework: {model.TargetFramework}");
        }
        if (!string.IsNullOrWhiteSpace(model.ExpressionLanguage)) {
            sb.AppendLine($"- Expression language: {model.ExpressionLanguage}");
        }
        if (!string.IsNullOrWhiteSpace(model.OutputType)) {
            sb.AppendLine($"- Output type: {model.OutputType}");
        }
        if (!string.IsNullOrWhiteSpace(model.MainWorkflow)) {
            sb.AppendLine($"- Main: `{model.MainWorkflow}`");
        }
        sb.AppendLine($"- Workflows: {xaml}");
        sb.AppendLine($"- Coded files: {cs}");
        sb.AppendLine($"- Dependencies: {deps}");
        sb.AppendLine();

        if (model.Packages.Count > 0) {
            sb.AppendLine("## Dependencies");
            sb.AppendLine();
            foreach (var package in model.Packages.Take(40)) {
                sb.AppendLine($"- {package.Id} ({package.Version})");
            }
            if (model.Packages.Count > 40) {
                sb.AppendLine($"- … {model.Packages.Count - 40} more");
            }
            sb.AppendLine();
        }

        if (model.Workflows.Count > 0) {
            sb.AppendLine("## Workflows");
            sb.AppendLine();
            foreach (var workflow in model.Workflows.Take(60)) {
                var marker = workflow.IsMain ? " (main)" : string.Empty;
                sb.AppendLine($"- `{workflow.FileName}`{marker}");
            }
            if (model.Workflows.Count > 60) {
                sb.AppendLine($"- … {model.Workflows.Count - 60} more");
            }
            sb.AppendLine();
        }

        if (model.CodedWorkflows.Count > 0) {
            sb.AppendLine("## Coded files");
            sb.AppendLine();
            foreach (var coded in model.CodedWorkflows.Take(40)) {
                sb.AppendLine($"- `{coded.FileName}`");
            }
            sb.AppendLine();
        }

        var lines = sb.ToString().Split('\n');
        if (lines.Length <= MaxLines) {
            return sb.ToString();
        }

        return string.Join('\n', lines.Take(MaxLines - 1)) + "\n<!-- truncated -->\n";
    }

    public string WrapBlock(string markdown) =>
        StartMarker + Environment.NewLine + markdown.TrimEnd() + Environment.NewLine + EndMarker + Environment.NewLine;

    public string SpliceAgentsMarkdown(string? existing, string generatedMarkdown) {
        var block = WrapBlock(generatedMarkdown);
        if (string.IsNullOrEmpty(existing)) {
            return block;
        }

        var start = existing.IndexOf(StartMarker, StringComparison.Ordinal);
        var end = existing.IndexOf(EndMarker, StringComparison.Ordinal);
        if (start >= 0 && end > start) {
            var afterEnd = end + EndMarker.Length;
            var suffix = existing[afterEnd..];
            if (suffix.StartsWith("\r\n")) {
                suffix = suffix[2..];
            } else if (suffix.StartsWith('\n')) {
                suffix = suffix[1..];
            }

            return existing[..start] + block + suffix;
        }

        var prefix = existing.TrimEnd();
        return prefix + Environment.NewLine + Environment.NewLine + block;
    }

    public void Sync(string projectPath, UiPathProjectModel model) {
        var markdown = RenderMarkdown(model);
        var contextPath = ProjectDocsPaths.ProjectContext(projectPath);
        _filesystem.CreateDirectory(Path.GetDirectoryName(contextPath)!);
        _filesystem.WriteAllText(contextPath, markdown);

        var agentsPath = ProjectDocsPaths.AgentsMd(projectPath);
        var existing = _filesystem.FileExists(agentsPath) ? _filesystem.ReadAllText(agentsPath) : null;
        _filesystem.WriteAllText(agentsPath, SpliceAgentsMarkdown(existing, markdown));
    }
}
