using System.Text.Json;
using System.Text.Json.Nodes;

namespace UiPath.Engineering.Mcp.Core.Canvas;

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
