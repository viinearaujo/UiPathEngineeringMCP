using System.Text.Json;
using System.Text.Json.Nodes;
using UiPath.Engineering.Mcp.Core;

namespace UiPath.Engineering.Mcp.Core.Canvas;

public sealed record CanvasNodePatch(string Id, string Explanation, IReadOnlyList<string> Decisions);

public sealed record CanvasSnapshotStatus(
    bool Written,
    bool OverviewWritten,
    int ExplainedCount,
    int TotalCount,
    int RemainingCount,
    IReadOnlyList<string> NextNodeIds);

public static class CanvasSnapshotDocument {
    public static bool TryRead(string json, out JsonObject? root, out string? error) {
        JsonNode? parsed;
        try {
            parsed = JsonNode.Parse(json);
        } catch (JsonException) {
            root = null;
            error = "invalid JSON";
            return false;
        }

        if (parsed is not JsonObject obj) {
            root = null;
            error = "invalid JSON";
            return false;
        }

        if (obj["schemaVersion"] is not JsonValue version
            || !version.TryGetValue<int>(out var number)
            || number != 1) {
            root = null;
            error = "schemaVersion must be the number 1.";
            return false;
        }

        if (obj["nodes"] is not JsonArray nodes) {
            root = null;
            error = "invalid JSON";
            return false;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in nodes) {
            var id = ReadString(node?["id"]);
            if (id is null) {
                continue;
            }

            if (!seen.Add(id)) {
                root = null;
                error = $"duplicate id '{id}'";
                return false;
            }
        }

        root = obj;
        error = null;
        return true;
    }

    public static IReadOnlyList<string> PriorityOrder(JsonObject root) {
        var ids = new List<string>();
        var idSet = new HashSet<string>(StringComparer.Ordinal);
        if (root["nodes"] is JsonArray nodes) {
            foreach (var node in nodes) {
                var id = ReadString(node?["id"]);
                if (id is not null && idSet.Add(id)) {
                    ids.Add(id);
                }
            }
        }

        var children = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var incoming = new HashSet<string>(StringComparer.Ordinal);
        if (root["edges"] is JsonArray edges) {
            foreach (var edge in edges) {
                if (edge is not JsonObject edgeObject) {
                    continue;
                }

                if (edgeObject["isResolved"] is not JsonValue resolved || !resolved.TryGetValue<bool>(out var isResolved) || !isResolved) {
                    continue;
                }

                var source = ReadString(edgeObject["sourceWorkflow"]);
                var target = ReadString(edgeObject["targetWorkflow"]);
                if (source is null || target is null || !idSet.Contains(source) || !idSet.Contains(target)) {
                    continue;
                }

                if (!children.TryGetValue(source, out var list)) {
                    list = [];
                    children[source] = list;
                }

                list.Add(target);
                incoming.Add(target);
            }
        }

        foreach (var list in children.Values) {
            var unique = list.Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToList();
            list.Clear();
            list.AddRange(unique);
        }

        var order = new List<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var entry = ReadString(root["project"]?["entryPoint"]);

        void Expand(Queue<string> queue) {
            while (queue.Count > 0) {
                var current = queue.Dequeue();
                if (!children.TryGetValue(current, out var kids)) {
                    continue;
                }

                foreach (var kid in kids) {
                    if (visited.Add(kid)) {
                        order.Add(kid);
                        queue.Enqueue(kid);
                    }
                }
            }
        }

        if (entry is not null && idSet.Contains(entry)) {
            var queue = new Queue<string>();
            visited.Add(entry);
            order.Add(entry);
            queue.Enqueue(entry);
            Expand(queue);
        } else {
            var queue = new Queue<string>();
            foreach (var id in ids.Where(id => !incoming.Contains(id)).OrderBy(id => id, StringComparer.Ordinal)) {
                if (visited.Add(id)) {
                    order.Add(id);
                    queue.Enqueue(id);
                }
            }

            Expand(queue);
        }

        foreach (var id in ids.OrderBy(id => id, StringComparer.Ordinal)) {
            if (visited.Add(id)) {
                order.Add(id);
            }
        }

        return order;
    }

    public static CanvasSnapshotStatus Status(JsonObject root, bool written) {
        var nodes = root["nodes"] as JsonArray;
        var total = nodes?.Count ?? 0;
        var explained = 0;
        var byId = Index(root);
        if (nodes is not null) {
            foreach (var node in nodes) {
                if (IsExplained(node as JsonObject)) {
                    explained++;
                }
            }
        }

        var next = new List<string>();
        foreach (var id in PriorityOrder(root)) {
            if (!byId.TryGetValue(id, out var node) || IsExplained(node)) {
                continue;
            }

            next.Add(id);
            if (next.Count == 3) {
                break;
            }
        }

        var overview = ReadString(root["project"]?["overview"]);
        return new CanvasSnapshotStatus(
            written,
            !string.IsNullOrWhiteSpace(overview),
            explained,
            total,
            total - explained,
            next);
    }

    public static string ToIndented(JsonObject root) =>
        root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    public static string? ValidatePatch(
        JsonObject root,
        JsonElement? overview,
        JsonElement? nodes,
        out string? overviewText,
        out bool setOverview,
        out List<CanvasNodePatch> patches) {
        overviewText = null;
        setOverview = false;
        patches = [];

        if (overview is { } overviewElement && overviewElement.ValueKind != JsonValueKind.Undefined) {
            if (overviewElement.ValueKind != JsonValueKind.String) {
                return "overview must be a string";
            }

            setOverview = true;
            overviewText = SecretRedactor.Redact(overviewElement.GetString() ?? string.Empty).Text;
        }

        if (nodes is not { } nodeElement
            || nodeElement.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            || (nodeElement.ValueKind == JsonValueKind.Array && nodeElement.GetArrayLength() == 0)) {
            return null;
        }

        if (nodeElement.ValueKind != JsonValueKind.Array) {
            return "decisions is required";
        }

        var index = Index(root);
        foreach (var item in nodeElement.EnumerateArray()) {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("id", out var idElement)
                || idElement.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(idElement.GetString())) {
                return "unknown node id";
            }

            var id = idElement.GetString()!;
            if (!index.TryGetValue(id, out var node)) {
                return "unknown node id";
            }

            if (!item.TryGetProperty("explanation", out var explanationElement)
                || explanationElement.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(explanationElement.GetString())) {
                return "explanation is blank";
            }

            if (!item.TryGetProperty("decisions", out var decisionsElement)
                || decisionsElement.ValueKind != JsonValueKind.Array) {
                return "decisions is required";
            }

            var decisions = new List<string>();
            foreach (var decision in decisionsElement.EnumerateArray()) {
                if (decision.ValueKind != JsonValueKind.String) {
                    return "decisions is required";
                }

                var trimmed = decision.GetString()!.Trim();
                if (trimmed.Length == 0) {
                    continue;
                }

                decisions.Add(SecretRedactor.Redact(trimmed).Text);
            }

            if (string.Equals(ReadString(node["kind"]), "coded", StringComparison.Ordinal) && decisions.Count > 0) {
                return "coded node decisions must be empty";
            }

            patches.Add(new CanvasNodePatch(
                id,
                SecretRedactor.Redact(explanationElement.GetString()!.Trim()).Text,
                decisions));
        }

        return null;
    }

    public static void Apply(
        JsonObject root,
        bool setOverview,
        string? overviewText,
        IReadOnlyList<CanvasNodePatch> patches) {
        if (setOverview) {
            if (root["project"] is not JsonObject project) {
                project = new JsonObject();
                root["project"] = project;
            }

            project["overview"] = overviewText ?? string.Empty;
        }

        var index = Index(root);
        foreach (var patch in patches) {
            var node = index[patch.Id];
            node["explanation"] = patch.Explanation;
            var decisions = new JsonArray();
            foreach (var decision in patch.Decisions) {
                decisions.Add(decision);
            }

            node["decisions"] = decisions;
        }
    }

    internal static Dictionary<string, JsonObject> Index(JsonObject root) {
        var index = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        if (root["nodes"] is not JsonArray nodes) {
            return index;
        }

        foreach (var node in nodes) {
            if (node is not JsonObject obj) {
                continue;
            }

            var id = ReadString(obj["id"]);
            if (id is not null) {
                index.TryAdd(id, obj);
            }
        }

        return index;
    }

    internal static string? ReadString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static bool IsExplained(JsonObject? node) {
        var text = ReadString(node?["explanation"]);
        return !string.IsNullOrWhiteSpace(text);
    }
}
