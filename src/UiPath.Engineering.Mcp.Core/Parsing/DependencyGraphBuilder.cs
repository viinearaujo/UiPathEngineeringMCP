using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Core.Parsing;

public sealed class DependencyGraphEdge
{
    public string Source { get; init; } = string.Empty;
    public string Target { get; init; } = string.Empty;
    public bool IsResolved { get; init; }
}

public sealed class DependencyGraphResult
{
    public List<DependencyGraphEdge> Edges { get; init; } = [];
    public List<List<string>> Cycles { get; init; } = [];
    public List<string> Orphans { get; init; } = [];
}

/// <summary>
/// Builds the workflow invocation graph (InvokeWorkflowFile edges) from parsed workflows,
/// matched by file name (case-insensitive). Detects cycles and workflows unreachable from Main.
/// </summary>
public static class DependencyGraphBuilder
{
    public static DependencyGraphResult Build(IReadOnlyList<WorkflowModel> workflows, string? mainWorkflow)
    {
        var result = new DependencyGraphResult();
        var byFileName = new Dictionary<string, WorkflowModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var workflow in workflows)
        {
            byFileName.TryAdd(workflow.FileName, workflow);
        }

        var adjacency = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var workflow in workflows)
        {
            foreach (var invoke in workflow.InvokeWorkflows)
            {
                if (string.IsNullOrWhiteSpace(invoke.TargetWorkflow))
                {
                    continue;
                }

                var resolved = byFileName.ContainsKey(invoke.TargetWorkflow);
                result.Edges.Add(new DependencyGraphEdge
                {
                    Source = workflow.FileName,
                    Target = invoke.TargetWorkflow,
                    IsResolved = resolved
                });

                if (resolved)
                {
                    if (!adjacency.TryGetValue(workflow.FileName, out var targets))
                    {
                        targets = [];
                        adjacency[workflow.FileName] = targets;
                    }
                    targets.Add(invoke.TargetWorkflow);
                }
            }
        }

        result.Cycles.AddRange(DetectCycles(byFileName.Keys, adjacency));
        result.Orphans.AddRange(FindOrphans(byFileName.Keys, adjacency, mainWorkflow));
        return result;
    }

    private static List<List<string>> DetectCycles(IEnumerable<string> nodes, Dictionary<string, List<string>> adjacency)
    {
        var cycles = new List<List<string>>();
        var seenCycleKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); // 0=unvisited, 1=in stack, 2=done
        var stack = new List<string>();

        void Dfs(string node)
        {
            state[node] = 1;
            stack.Add(node);
            if (adjacency.TryGetValue(node, out var targets))
            {
                foreach (var target in targets)
                {
                    var targetState = state.GetValueOrDefault(target);
                    if (targetState == 0)
                    {
                        Dfs(target);
                    }
                    else if (targetState == 1)
                    {
                        var cycle = stack.Skip(stack.IndexOf(target)).Concat([target]).ToList();
                        // Canonical key so the same cycle is reported once regardless of entry point.
                        var ring = cycle.Take(cycle.Count - 1).OrderBy(n => n, StringComparer.OrdinalIgnoreCase);
                        if (seenCycleKeys.Add(string.Join("|", ring)))
                        {
                            cycles.Add(cycle);
                        }
                    }
                }
            }
            stack.RemoveAt(stack.Count - 1);
            state[node] = 2;
        }

        foreach (var node in nodes)
        {
            if (state.GetValueOrDefault(node) == 0)
            {
                Dfs(node);
            }
        }

        return cycles;
    }

    private static List<string> FindOrphans(IEnumerable<string> nodes, Dictionary<string, List<string>> adjacency, string? mainWorkflow)
    {
        var reachable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (mainWorkflow is not null)
        {
            var queue = new Queue<string>();
            if (reachable.Add(mainWorkflow))
            {
                queue.Enqueue(mainWorkflow);
            }
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (adjacency.TryGetValue(current, out var targets))
                {
                    foreach (var target in targets)
                    {
                        if (reachable.Add(target))
                        {
                            queue.Enqueue(target);
                        }
                    }
                }
            }
        }

        return nodes.Where(n => !reachable.Contains(n)).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
