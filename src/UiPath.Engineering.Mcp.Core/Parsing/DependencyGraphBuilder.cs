using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Core.Parsing;

public sealed class DependencyGraphEdge {
    public string Source { get; init; } = string.Empty;
    public string Target { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public bool IsResolved { get; init; }
    public List<ArgumentMappingModel> ArgumentMappings { get; init; } = [];
}

public sealed class DependencyGraphResult {
    public List<DependencyGraphEdge> Edges { get; init; } = [];
    public List<List<string>> Cycles { get; init; } = [];
    public List<string> Orphans { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
    public IReadOnlyDictionary<string, List<DependencyGraphEdge>> CallersIndex { get; init; }
        = new Dictionary<string, List<DependencyGraphEdge>>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Builds the workflow invocation graph (InvokeWorkflowFile edges) from parsed workflows,
/// matched by project-relative path (case-insensitive), with a unique file-name fallback.
/// Detects cycles and workflows unreachable from Main.
/// </summary>
public static class DependencyGraphBuilder {
    public static DependencyGraphResult Build(IReadOnlyList<WorkflowModel> workflows, string? mainWorkflow) {
        var result = new DependencyGraphResult();
        var byRelative = new Dictionary<string, WorkflowModel>(StringComparer.OrdinalIgnoreCase);
        var byFileName = new Dictionary<string, List<WorkflowModel>>(StringComparer.OrdinalIgnoreCase);
        foreach (var workflow in workflows) {
            var identity = WorkflowPath.Identity(workflow);
            byRelative.TryAdd(identity, workflow);
            var name = workflow.FileName.Length > 0 ? workflow.FileName : Path.GetFileName(identity);
            if (!byFileName.TryGetValue(name, out var list)) {
                list = [];
                byFileName[name] = list;
            }

            list.Add(workflow);
        }

        var adjacency = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var workflow in workflows) {
            var source = WorkflowPath.Identity(workflow);
            foreach (var invoke in workflow.InvokeWorkflows) {
                if (string.IsNullOrWhiteSpace(invoke.TargetWorkflow)) {
                    continue;
                }

                var resolved = TryResolveTarget(
                    invoke.TargetWorkflow, byRelative, byFileName, result.Warnings, out var targetIdentity);
                result.Edges.Add(new DependencyGraphEdge {
                    Source = source,
                    Target = resolved ? targetIdentity : WorkflowPath.NormalizeRef(invoke.TargetWorkflow),
                    DisplayName = invoke.DisplayName,
                    IsResolved = resolved,
                    ArgumentMappings = [.. invoke.ArgumentMappings]
                });

                if (resolved) {
                    if (!adjacency.TryGetValue(source, out var targets)) {
                        targets = [];
                        adjacency[source] = targets;
                    }
                    targets.Add(targetIdentity);
                }
            }
        }

        var callers = new Dictionary<string, List<DependencyGraphEdge>>(StringComparer.OrdinalIgnoreCase);
        foreach (var edge in result.Edges) {
            if (!callers.TryGetValue(edge.Target, out var incoming)) {
                incoming = [];
                callers[edge.Target] = incoming;
            }
            incoming.Add(edge);
        }

        var resolvedMain = ResolveMain(mainWorkflow, byRelative, byFileName);
        return new DependencyGraphResult {
            Edges = result.Edges,
            CallersIndex = callers,
            Cycles = DetectCycles(byRelative.Keys, adjacency),
            Orphans = FindOrphans(byRelative.Keys, adjacency, resolvedMain),
            Warnings = result.Warnings
        };
    }

    private static string? ResolveMain(
        string? mainWorkflow,
        Dictionary<string, WorkflowModel> byRelative,
        Dictionary<string, List<WorkflowModel>> byFileName) {
        if (string.IsNullOrWhiteSpace(mainWorkflow)) {
            return null;
        }

        return TryResolveTarget(mainWorkflow, byRelative, byFileName, warnings: null, out var identity)
            ? identity
            : WorkflowPath.NormalizeRef(mainWorkflow);
    }

    private static bool TryResolveTarget(
        string rawTarget,
        Dictionary<string, WorkflowModel> byRelative,
        Dictionary<string, List<WorkflowModel>> byFileName,
        List<string>? warnings,
        out string identity) {
        var normalized = WorkflowPath.NormalizeRef(rawTarget);
        identity = normalized;
        if (normalized.Length == 0) {
            return false;
        }

        if (byRelative.TryGetValue(normalized, out var exact)) {
            identity = WorkflowPath.Identity(exact);
            return true;
        }

        var fileName = Path.GetFileName(normalized);
        if (!byFileName.TryGetValue(fileName, out var matches) || matches.Count != 1) {
            return false;
        }

        identity = WorkflowPath.Identity(matches[0]);
        if (!string.Equals(identity, normalized, StringComparison.OrdinalIgnoreCase)) {
            warnings?.Add(
                $"Invoke '{rawTarget}' matched '{identity}' by file name; prefer a project-relative path.");
        }

        return true;
    }

    private static List<List<string>> DetectCycles(IEnumerable<string> nodes, Dictionary<string, List<string>> adjacency) {
        var cycles = new List<List<string>>();
        var seenCycleKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); // 0=unvisited, 1=in stack, 2=done
        var stack = new List<string>();

        void Dfs(string node) {
            state[node] = 1;
            stack.Add(node);
            if (adjacency.TryGetValue(node, out var targets)) {
                foreach (var target in targets) {
                    var targetState = state.GetValueOrDefault(target);
                    if (targetState == 0) {
                        Dfs(target);
                    } else if (targetState == 1) {
                        var cycle = stack.Skip(stack.IndexOf(target)).Concat([target]).ToList();
                        // Canonical key so the same cycle is reported once regardless of entry point.
                        var ring = cycle.Take(cycle.Count - 1).OrderBy(n => n, StringComparer.OrdinalIgnoreCase);
                        if (seenCycleKeys.Add(string.Join("|", ring))) {
                            cycles.Add(cycle);
                        }
                    }
                }
            }
            stack.RemoveAt(stack.Count - 1);
            state[node] = 2;
        }

        foreach (var node in nodes) {
            if (state.GetValueOrDefault(node) == 0) {
                Dfs(node);
            }
        }

        return cycles;
    }

    private static List<string> FindOrphans(IEnumerable<string> nodes, Dictionary<string, List<string>> adjacency, string? mainWorkflow) {
        var reachable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (mainWorkflow is not null) {
            var queue = new Queue<string>();
            if (reachable.Add(mainWorkflow)) {
                queue.Enqueue(mainWorkflow);
            }
            while (queue.Count > 0) {
                var current = queue.Dequeue();
                if (adjacency.TryGetValue(current, out var targets)) {
                    foreach (var target in targets) {
                        if (reachable.Add(target)) {
                            queue.Enqueue(target);
                        }
                    }
                }
            }
        }

        return nodes.Where(n => !reachable.Contains(n)).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
