using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Core.Authoring;

/// <summary>
/// Rule 20 structure-first validation for Flowchart and StateMachine graphs:
/// every link must resolve to a declared node, and an unreferenced node is an orphan.
/// </summary>
public static class SpecGraphValidator {
    public static void ValidateFlowchart(FlowchartSpec flowchart, string path, List<ToolError> errors) {
        var nodes = flowchart.Nodes ?? [];
        if (nodes.Count == 0) {
            errors.Add(new ToolError(
                ToolErrorCodes.SpecMissingRequiredProperty,
                $"Flowchart at {path} declares no nodes.",
                "Add \"nodes\": [ { \"id\": \"1\", \"type\": \"Step\", \"activity\": { ... } } ] and a \"start\" id."));
            return;
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < nodes.Count; i++) {
            var node = nodes[i];
            if (string.IsNullOrWhiteSpace(node.Id)) {
                errors.Add(new ToolError(
                    ToolErrorCodes.SpecMissingRequiredProperty,
                    $"Flowchart node at {path}.flowchart.nodes[{i}] is missing required property \"id\".",
                    "Set \"id\" to a stable node id, e.g. \"1\"; links address nodes by it."));
                continue;
            }

            if (!ids.Add(node.Id)) {
                errors.Add(new ToolError(
                    ToolErrorCodes.SpecInvalidNesting,
                    $"Flowchart at {path} declares two nodes with id \"{node.Id}\".",
                    "Give every node a unique \"id\"."));
            }
        }

        if (string.IsNullOrWhiteSpace(flowchart.Start)) {
            errors.Add(new ToolError(
                ToolErrorCodes.SpecMissingRequiredProperty,
                $"Flowchart at {path} is missing required property \"start\".",
                "Set \"start\" to the id of the first node."));
        }

        var targets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in nodes) {
            var kind = (node.Type ?? "Step").Trim().ToLowerInvariant();
            switch (kind) {
                case "decision":
                    if (string.IsNullOrWhiteSpace(node.Condition)) {
                        errors.Add(MissingNodeProperty(node, "condition", path));
                    }

                    if (string.IsNullOrWhiteSpace(node.True) && string.IsNullOrWhiteSpace(node.False)) {
                        errors.Add(new ToolError(
                            ToolErrorCodes.SpecMissingRequiredProperty,
                            $"FlowDecision \"{node.Id}\" at {path} has neither a \"true\" nor a \"false\" target.",
                            "Set at least one of \"true\" / \"false\" to a node id."));
                    }

                    Track(targets, node.True);
                    Track(targets, node.False);
                    break;

                case "switch":
                    if (string.IsNullOrWhiteSpace(node.Expression)) {
                        errors.Add(MissingNodeProperty(node, "expression", path));
                    }

                    foreach (var switchCase in node.Cases ?? []) {
                        Track(targets, switchCase.Target);
                    }

                    Track(targets, node.Default);
                    break;

                default:
                    // A step with no "next" ends the flow — a valid terminal node.
                    Track(targets, node.Next);
                    break;
            }
        }

        Track(targets, flowchart.Start);
        CheckLinks(ids, targets, "Flowchart", path, errors);
    }

    public static void ValidateStateMachine(StateMachineSpec machine, string path, List<ToolError> errors) {
        var states = machine.States ?? [];
        if (states.Count == 0) {
            errors.Add(new ToolError(
                ToolErrorCodes.SpecMissingRequiredProperty,
                $"StateMachine at {path} declares no states.",
                "Add \"states\": [ { \"id\": \"1\", \"transitions\": [ { \"to\": \"2\" } ] } ] and a \"start\" id."));
            return;
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < states.Count; i++) {
            var state = states[i];
            if (string.IsNullOrWhiteSpace(state.Id)) {
                errors.Add(new ToolError(
                    ToolErrorCodes.SpecMissingRequiredProperty,
                    $"State at {path}.stateMachine.states[{i}] is missing required property \"id\".",
                    "Set \"id\" to a stable state id, e.g. \"1\"."));
                continue;
            }

            if (!ids.Add(state.Id)) {
                errors.Add(new ToolError(
                    ToolErrorCodes.SpecInvalidNesting,
                    $"StateMachine at {path} declares two states with id \"{state.Id}\".",
                    "Give every state a unique \"id\"."));
            }
        }

        if (string.IsNullOrWhiteSpace(machine.Start)) {
            errors.Add(new ToolError(
                ToolErrorCodes.SpecMissingRequiredProperty,
                $"StateMachine at {path} is missing required property \"start\".",
                "Set \"start\" to the id of the initial state."));
        }

        var targets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var state in states) {
            for (var j = 0; j < (state.Transitions?.Count ?? 0); j++) {
                var transition = state.Transitions![j];
                if (string.IsNullOrWhiteSpace(transition.To)) {
                    errors.Add(new ToolError(
                        ToolErrorCodes.SpecMissingRequiredProperty,
                        $"Transition at {path}.stateMachine.states[{state.Id}].transitions[{j}] is missing required property \"to\".",
                        "Set \"to\" to the id of the destination state."));
                    continue;
                }

                Track(targets, transition.To);
            }
        }

        Track(targets, machine.Start);
        CheckLinks(ids, targets, "StateMachine", path, errors);
    }

    private static ToolError MissingNodeProperty(FlowNodeSpec node, string property, string path) => new(
        ToolErrorCodes.SpecMissingRequiredProperty,
        $"Flowchart node \"{node.Id}\" at {path} is missing required property \"{property}\".",
        $"Set \"{property}\" on the node (a {node.Type} needs it).");

    private static void Track(HashSet<string> targets, string? id) {
        if (!string.IsNullOrWhiteSpace(id)) {
            targets.Add(id.Trim());
        }
    }

    // A link to an undeclared node is a file Studio cannot load; an unreferenced
    // node is an orphan the designer draws but nothing ever runs.
    private static void CheckLinks(
        HashSet<string> ids, HashSet<string> targets, string kind, string path, List<ToolError> errors) {
        foreach (var target in targets) {
            if (!ids.Contains(target)) {
                errors.Add(new ToolError(
                    ToolErrorCodes.SpecInvalidNesting,
                    $"{kind} at {path} links to node id \"{target}\", which is not declared.",
                    "Every start/next/true/false/to target must name a declared node id."));
            }
        }

        foreach (var id in ids) {
            if (!targets.Contains(id)) {
                errors.Add(new ToolError(
                    ToolErrorCodes.SpecInvalidNesting,
                    $"{kind} at {path} declares node \"{id}\", but no other node or the start links to it, so it is an orphan.",
                    "Wire it with a start/next/true/false/to reference, or remove it."));
            }
        }
    }
}
