using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Core.Parsing;

namespace UiPath.Engineering.Mcp.Core.Canvas;

public static class CanvasSnapshotPath {
    public const string RelativeDirectory = ".canvas";
    public const string FileName = "snapshot.json";

    public static string ForProject(string projectPath) =>
        Path.Combine(projectPath, RelativeDirectory, FileName);
}

public sealed class CanvasSnapshotDraft {
    public string? Error { get; init; }
    public string Json { get; init; } = string.Empty;
    public int NodeCount { get; init; }
    public int EdgeCount { get; init; }
    public string GeneratedAt { get; init; } = string.Empty;
}

public static class CanvasSnapshotSkeleton {
    public static string FormatGeneratedAt(DateTime utc) {
        var stamp = utc.Kind == DateTimeKind.Utc ? utc : DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        return stamp.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    }

    public static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static CanvasSnapshotDraft Build(
        UiPathProjectModel model,
        string projectPath,
        string mcpVersion,
        Func<string, byte[]> readBytes,
        DateTime generatedAtUtc) {
        var generatedAt = FormatGeneratedAt(generatedAtUtc);
        var nodes = new JsonArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var nodeIds = new List<string>();

        foreach (var workflow in model.Workflows.Where(IsXaml)) {
            var id = WorkflowPath.Identity(workflow);
            if (!seen.Add(id)) {
                return Duplicate(id);
            }

            nodeIds.Add(id);
            nodes.Add(Node(
                id,
                "xaml",
                Sha256Hex(readBytes(workflow.FilePath)),
                workflow.Arguments,
                workflow.ExceptionHandlers.Any(handler => handler.HasGlobalHandler),
                workflow.HasParseError ? workflow.ParseError : null));
        }

        foreach (var coded in model.CodedWorkflows) {
            if (!string.Equals(coded.Kind, CodedFileKind.Workflow, StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            var id = WorkflowPath.ToRelativePath(projectPath, coded.FilePath);
            if (!seen.Add(id)) {
                return Duplicate(id);
            }

            nodeIds.Add(id);
            nodes.Add(Node(
                id,
                "coded",
                Sha256Hex(readBytes(coded.FilePath)),
                coded.EntryArguments,
                coded.EntryHasTryCatch == true,
                coded.HasParseError ? coded.ParseError : null));
        }

        var entryPoint = ResolveEntry(model.MainWorkflow, nodeIds);
        var project = new JsonObject {
            ["name"] = model.ProjectName,
            ["additionalEntryPoints"] = new JsonArray(),
            ["overview"] = ""
        };
        project["entryPoint"] = entryPoint is null ? JsonNull() : JsonValue.Create(entryPoint)!;

        var root = new JsonObject {
            ["schemaVersion"] = 1,
            ["generatedAt"] = generatedAt,
            ["generator"] = new JsonObject { ["mcpVersion"] = mcpVersion },
            ["project"] = project,
            ["nodes"] = nodes,
            ["edges"] = new JsonArray()
        };

        return new CanvasSnapshotDraft {
            Json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            NodeCount = nodes.Count,
            EdgeCount = 0,
            GeneratedAt = generatedAt
        };
    }

    internal static string? ResolveEntry(string? main, IReadOnlyList<string> nodeIds) {
        if (string.IsNullOrWhiteSpace(main)) {
            return null;
        }

        var normalized = WorkflowPath.NormalizeRef(main);
        if (normalized.Length == 0) {
            return null;
        }

        return nodeIds.FirstOrDefault(id => string.Equals(id, normalized, StringComparison.OrdinalIgnoreCase))
            ?? normalized;
    }

    private static CanvasSnapshotDraft Duplicate(string id) => new() { Error = $"duplicate id '{id}'" };

    private static bool IsXaml(WorkflowModel workflow) =>
        workflow.FileName.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)
        || workflow.RelativePath.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase);

    private static JsonObject Node(
        string id,
        string kind,
        string sha256,
        IEnumerable<ArgumentModel> arguments,
        bool hasExceptionHandler,
        string? parseError) {
        var argumentArray = new JsonArray();
        foreach (var argument in arguments) {
            argumentArray.Add(new JsonObject {
                ["name"] = argument.Name,
                ["direction"] = argument.Direction,
                ["type"] = argument.Type
            });
        }

        var node = new JsonObject {
            ["id"] = id,
            ["kind"] = kind,
            ["sha256"] = sha256,
            ["arguments"] = argumentArray,
            ["hasExceptionHandler"] = hasExceptionHandler,
            ["explanation"] = "",
            ["decisions"] = new JsonArray()
        };
        node["parseError"] = parseError is null ? JsonNull() : JsonValue.Create(parseError)!;
        return node;
    }

    private static JsonNode JsonNull() => JsonNode.Parse("null")!;
}
