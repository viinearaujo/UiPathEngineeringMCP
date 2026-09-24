using System.Text.Json;

namespace UiPath.Engineering.Mcp.Providers.UiPathCli;

/// <summary>One node (App, Screen, or Element) in an Object Repository tree.</summary>
public sealed record ObjectRepositoryNode {
    public string Name { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string? TaxonomyType { get; init; }
    public string? Description { get; init; }

    /// <summary>Opaque OR reference token the UIA activities bind to.</summary>
    public string? Reference { get; init; }

    /// <summary>Dotted path of ancestor names, e.g. "CourtView/General Application".</summary>
    public string Path { get; init; } = string.Empty;
    public int Depth { get; init; }
    public List<ObjectRepositoryNode> Children { get; init; } = [];
}

/// <summary>
/// The Object Repository read result: the apps → screens → elements tree plus a flat
/// index for name lookups, and counts so a caller can see the size before reading it.
/// </summary>
public sealed record ObjectRepositoryResult {
    public List<ObjectRepositoryNode> Nodes { get; init; } = [];
    public List<ObjectRepositoryNode> Flat { get; init; } = [];
    public int Apps { get; init; }
    public int Screens { get; init; }
    public int Elements { get; init; }

    /// <summary>Library name when the result came from <c>get-library-object-repository</c>.</summary>
    public string? Library { get; init; }
}

/// <summary>
/// Parses the Object Repository read verbs. <c>uip rpa get-object-repository</c> returns
/// <c>Data</c> as an array of app nodes; <c>uip rpa get-library-object-repository</c> groups
/// the same node shape per library. Node property casing differs between CLI versions, so
/// every lookup is case-insensitive.
/// </summary>
public static class ObjectRepositoryParser {
    public static ObjectRepositoryResult ParseProject(JsonElement? data) {
        if (data is not { } element) {
            return new ObjectRepositoryResult();
        }

        var nodes = element.ValueKind switch {
            JsonValueKind.Array => ReadNodes(element, parentPath: null, depth: 0),
            JsonValueKind.Object => ReadObjectNodes(element, parentPath: null, depth: 0),
            _ => []
        };

        return Summarize(nodes, library: null);
    }

    /// <summary>
    /// Reads the library-grouped payload. Returns one result per library; packages without an
    /// Object Repository are omitted by the CLI and therefore absent here.
    /// </summary>
    public static IReadOnlyList<ObjectRepositoryResult> ParseLibraries(JsonElement? data) {
        if (data is not { } element) {
            return [];
        }

        var results = new List<ObjectRepositoryResult>();

        if (element.ValueKind == JsonValueKind.Array) {
            // Either a flat node array (single library) or an array of { library, ... } groups.
            var groups = element.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.Object && HasLibraryName(item))
                .ToList();

            if (groups.Count > 0) {
                foreach (var group in groups) {
                    var name = CliEnvelopeParser.GetString(group, "library", "libraryName", "name", "packageId");
                    results.Add(Summarize(ReadGroupNodes(group, name), name));
                }

                return results;
            }

            var flat = ReadNodes(element, parentPath: null, depth: 0);
            // An empty array means no library carried a repository (the CLI omits those), which is
            // different from one library with an empty tree — report it as no libraries.
            return flat.Count > 0 ? [Summarize(flat, library: null)] : [];
        }

        if (element.ValueKind == JsonValueKind.Object) {
            // Keyed by library: { "Acme.UiLib": [ ...nodes ] } or { libraries: [...] }.
            if (CliEnvelopeParser.TryGetProperty(element, "libraries", out var libraries)
                && libraries.ValueKind == JsonValueKind.Array) {
                foreach (var library in libraries.EnumerateArray()) {
                    if (library.ValueKind != JsonValueKind.Object) {
                        continue;
                    }

                    var name = CliEnvelopeParser.GetString(library, "library", "libraryName", "name", "packageId");
                    results.Add(Summarize(ReadGroupNodes(library, name), name));
                }

                return results;
            }

            if (HasLibraryName(element)) {
                var name = CliEnvelopeParser.GetString(element, "library", "libraryName", "name", "packageId");
                return [Summarize(ReadGroupNodes(element, name), name)];
            }

            foreach (var property in element.EnumerateObject()) {
                if (property.Value.ValueKind != JsonValueKind.Array) {
                    continue;
                }

                results.Add(Summarize(ReadNodes(property.Value, parentPath: null, depth: 0), property.Name));
            }
        }

        return results;
    }

    private static bool HasLibraryName(JsonElement element) =>
        CliEnvelopeParser.GetString(element, "library", "libraryName", "packageId") is not null;

    private static List<ObjectRepositoryNode> ReadGroupNodes(JsonElement group, string? library) {
        foreach (var name in new[] { "objectRepository", "nodes", "apps", "applications", "children", "data" }) {
            if (CliEnvelopeParser.TryGetProperty(group, name, out var nodes)
                && nodes.ValueKind == JsonValueKind.Array) {
                return ReadNodes(nodes, parentPath: library, depth: 0);
            }
        }

        return [];
    }

    // A library group's node array may live under a differently named property, so fall
    // back to the first array property that holds node-shaped objects.
    private static List<ObjectRepositoryNode> ReadObjectNodes(JsonElement element, string? parentPath, int depth) {
        var nodes = new List<ObjectRepositoryNode>();
        foreach (var property in element.EnumerateObject()) {
            if (property.Value.ValueKind == JsonValueKind.Array) {
                nodes.AddRange(ReadNodes(property.Value, parentPath, depth));
            } else if (property.Value.ValueKind == JsonValueKind.Object && IsNode(property.Value)) {
                nodes.Add(ReadNode(property.Value, property.Name, parentPath, depth));
            }
        }

        return nodes;
    }

    private static List<ObjectRepositoryNode> ReadNodes(JsonElement array, string? parentPath, int depth) {
        var nodes = new List<ObjectRepositoryNode>();
        if (array.ValueKind != JsonValueKind.Array) {
            return nodes;
        }

        foreach (var item in array.EnumerateArray()) {
            if (item.ValueKind != JsonValueKind.Object) {
                continue;
            }

            var name = CliEnvelopeParser.GetString(item, "name", "Name", "displayName", "DisplayName") ?? string.Empty;
            nodes.Add(ReadNode(item, name, parentPath, depth));
        }

        return nodes;
    }

    private static ObjectRepositoryNode ReadNode(JsonElement item, string name, string? parentPath, int depth) {
        var node = new ObjectRepositoryNode {
            Name = name,
            Type = CliEnvelopeParser.GetString(item, "type", "Type") ?? string.Empty,
            TaxonomyType = CliEnvelopeParser.GetString(item, "taxonomyType", "TaxonomyType"),
            Description = CliEnvelopeParser.GetString(item, "description", "Description"),
            Reference = CliEnvelopeParser.GetString(item, "reference", "Reference"),
            Path = parentPath is null || parentPath.Length == 0 ? name : $"{parentPath}/{name}",
            Depth = depth
        };

        if (CliEnvelopeParser.TryGetProperty(item, "children", out var children)
            && children.ValueKind == JsonValueKind.Array) {
            node.Children.AddRange(ReadNodes(children, node.Path, depth + 1));
        }

        return node;
    }

    private static bool IsNode(JsonElement item) =>
        item.ValueKind == JsonValueKind.Object
        && CliEnvelopeParser.GetString(item, "type", "Type", "name", "Name") is not null;

    private static ObjectRepositoryResult Summarize(List<ObjectRepositoryNode> nodes, string? library) {
        var flat = new List<ObjectRepositoryNode>();
        var apps = 0;
        var screens = 0;
        var elements = 0;

        void Walk(IEnumerable<ObjectRepositoryNode> items) {
            foreach (var item in items) {
                flat.Add(item);
                if (string.Equals(item.Type, "App", StringComparison.OrdinalIgnoreCase)) {
                    apps++;
                } else if (string.Equals(item.Type, "Screen", StringComparison.OrdinalIgnoreCase)) {
                    screens++;
                } else if (string.Equals(item.Type, "Element", StringComparison.OrdinalIgnoreCase)) {
                    elements++;
                }

                Walk(item.Children);
            }
        }

        Walk(nodes);

        return new ObjectRepositoryResult {
            Nodes = nodes,
            Flat = flat,
            Apps = apps,
            Screens = screens,
            Elements = elements,
            Library = library
        };
    }
}
