using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace UiPath.Engineering.Mcp.AgentEvals;

internal static class AssertionChecker {
    public static (bool Passed, string Detail) Evaluate(
        string projectPath,
        IReadOnlyList<AgentAssertion> assertions) {
        var failures = new List<string>();
        foreach (var assertion in assertions) {
            switch (assertion.Type.Trim().ToLowerInvariant()) {
                case "fileexists":
                    CheckFileExists(projectPath, assertion, failures);
                    break;
                case "filecontains":
                    CheckFileContains(projectPath, assertion, failures);
                    break;
                case "plantaskstatus":
                    CheckPlanTaskStatus(projectPath, assertion, failures);
                    break;
                default:
                    failures.Add($"Unknown assertion type '{assertion.Type}'.");
                    break;
            }
        }

        return failures.Count == 0
            ? (true, "all assertions passed")
            : (false, string.Join("; ", failures));
    }

    private static void CheckFileExists(string projectPath, AgentAssertion assertion, List<string> failures) {
        if (string.IsNullOrWhiteSpace(assertion.Path)) {
            failures.Add("fileExists missing path");
            return;
        }

        var full = Resolve(projectPath, assertion.Path);
        if (!File.Exists(full)) {
            failures.Add($"fileExists failed: {assertion.Path}");
        }
    }

    private static void CheckFileContains(string projectPath, AgentAssertion assertion, List<string> failures) {
        if (string.IsNullOrWhiteSpace(assertion.Path) || assertion.Snippet is null) {
            failures.Add("fileContains requires path and snippet");
            return;
        }

        var full = Resolve(projectPath, assertion.Path);
        if (!File.Exists(full)) {
            failures.Add($"fileContains missing file: {assertion.Path}");
            return;
        }

        var text = File.ReadAllText(full);
        if (!text.Contains(assertion.Snippet, StringComparison.Ordinal)) {
            failures.Add($"fileContains failed: {assertion.Path} missing snippet");
        }
    }

    private static void CheckPlanTaskStatus(string projectPath, AgentAssertion assertion, List<string> failures) {
        if (string.IsNullOrWhiteSpace(assertion.TaskId) || string.IsNullOrWhiteSpace(assertion.Status)) {
            failures.Add("planTaskStatus requires taskId and status");
            return;
        }

        var planPath = Path.Combine(projectPath, "docs", "implementation-plan.json");
        if (!File.Exists(planPath)) {
            failures.Add("planTaskStatus: docs/implementation-plan.json missing");
            return;
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(planPath));
        if (!doc.RootElement.TryGetProperty("Tasks", out var tasks)
            && !doc.RootElement.TryGetProperty("tasks", out tasks)) {
            failures.Add("planTaskStatus: Tasks array missing");
            return;
        }

        JsonElement? match = null;
        foreach (var task in tasks.EnumerateArray()) {
            var id = task.TryGetProperty("Id", out var idProp)
                ? idProp.GetString()
                : task.TryGetProperty("id", out var idCamel) ? idCamel.GetString() : null;
            if (string.Equals(id, assertion.TaskId, StringComparison.OrdinalIgnoreCase)) {
                match = task;
                break;
            }
        }

        if (match is null) {
            failures.Add($"planTaskStatus: task '{assertion.TaskId}' not found");
            return;
        }

        var status = match.Value.TryGetProperty("Status", out var st)
            ? st.GetString()
            : match.Value.TryGetProperty("status", out var stCamel) ? stCamel.GetString() : null;

        if (!string.Equals(status, assertion.Status, StringComparison.OrdinalIgnoreCase)) {
            failures.Add(
                $"planTaskStatus: task '{assertion.TaskId}' expected '{assertion.Status}' got '{status}'");
        }
    }

    private static string Resolve(string projectPath, string relative) =>
        Path.GetFullPath(Path.Combine(projectPath, relative.Replace('/', Path.DirectorySeparatorChar)));
}

internal static class ScorecardWriter {
    public static void Write(string repoRoot, IReadOnlyList<AgentTaskResult> results, bool skipped) {
        var path = Path.Combine(repoRoot, "evals", "agent", "scorecard.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var sb = new StringBuilder();
        sb.AppendLine("# Agent eval scorecard");
        sb.AppendLine();
        sb.AppendLine($"Generated (UTC): {DateTimeOffset.UtcNow:O}");
        sb.AppendLine();

        if (skipped) {
            sb.AppendLine("Skipped: AGENT_EVAL_API_KEY / AGENT_EVAL_MODEL not set.");
            sb.AppendLine();
            sb.AppendLine("Set those env vars (optional AGENT_EVAL_BASE_URL) and run:");
            sb.AppendLine();
            sb.AppendLine("```");
            sb.AppendLine("dotnet test tests/UiPath.Engineering.Mcp.AgentEvals --filter Category=AgentEval");
            sb.AppendLine("```");
            File.WriteAllText(path, sb.ToString());
            return;
        }

        sb.AppendLine("| id | result | rounds | detail |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var r in results) {
            var detail = r.Detail.Replace("|", "\\|", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
            sb.AppendLine($"| {r.Id} | {(r.Passed ? "PASS" : "FAIL")} | {r.ToolRounds} | {detail} |");
        }

        var passed = results.Count(r => r.Passed);
        sb.AppendLine();
        sb.AppendLine("## Totals");
        sb.AppendLine();
        sb.AppendLine($"- tasks passed: {passed}/{results.Count}");
        File.WriteAllText(path, sb.ToString());
    }
}
