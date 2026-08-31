using System.Text.Json;
using System.Text.Json.Nodes;

namespace UiPath.Engineering.Mcp.Core.Docs;

public sealed class ProjectJsonPatchResult {
    public bool Success { get; init; }
    public string? Error { get; init; }
    public string? ErrorCode { get; init; }
    public string? UpdatedJson { get; init; }
    public string? Summary { get; init; }
}

public static class ProjectJsonPatcher {
    public const string AddEntryPoint = "add_entry_point";
    public const string RemoveEntryPoint = "remove_entry_point";
    public const string UpsertDependency = "upsert_dependency";
    public const string RemoveDependency = "remove_dependency";
    public const string UpsertFileInfo = "upsert_file_info";
    public const string RemoveFileInfo = "remove_file_info";
    public const string SetExceptionHandler = "set_exception_handler";
    public const string SetRuntimeOption = "set_runtime_option";

    public static readonly string[] ImmutableKeys = ["expressionLanguage", "targetFramework", "schemaVersion"];

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public static ProjectJsonPatchResult Apply(
        string json,
        string operation,
        string? filePath = null,
        string? packageId = null,
        string? version = null,
        string? key = null,
        string? value = null) {

        JsonObject root;
        try {
            root = JsonNode.Parse(json) as JsonObject
                ?? throw new InvalidDataException("project.json root is not an object.");
        } catch (Exception ex) when (ex is JsonException or InvalidDataException) {
            return Fail($"project.json could not be parsed: {ex.Message}");
        }

        var normalized = operation?.Trim().ToLowerInvariant();
        var result = normalized switch {
            AddEntryPoint => AddOrRemoveEntryPoint(root, filePath, add: true),
            RemoveEntryPoint => AddOrRemoveEntryPoint(root, filePath, add: false),
            UpsertDependency => UpsertOrRemoveDependency(root, packageId, version, remove: false),
            RemoveDependency => UpsertOrRemoveDependency(root, packageId, version: null, remove: true),
            UpsertFileInfo => UpsertOrRemoveFileInfo(root, filePath, remove: false),
            RemoveFileInfo => UpsertOrRemoveFileInfo(root, filePath, remove: true),
            SetExceptionHandler => SetExceptionHandlerWorkflow(root, filePath),
            SetRuntimeOption => SetRuntimeOptionValue(root, key, value),
            _ => Fail($"Unknown operation '{operation}'.")
        };

        if (!result.Success) {
            return result;
        }

        return new ProjectJsonPatchResult {
            Success = true,
            UpdatedJson = root.ToJsonString(WriteOptions),
            Summary = result.Summary
        };
    }

    private static ProjectJsonPatchResult AddOrRemoveEntryPoint(JsonObject root, string? filePath, bool add) {
        if (string.IsNullOrWhiteSpace(filePath)) {
            return Fail("filePath is required.");
        }

        var entryPoints = root["entryPoints"] as JsonArray ?? new JsonArray();
        root["entryPoints"] = entryPoints;

        var existing = FindObject(entryPoints, "filePath", filePath);
        if (add) {
            if (existing is not null) {
                return new ProjectJsonPatchResult { Success = true, Summary = $"Entry point '{filePath}' already present." };
            }

            entryPoints.Add(new JsonObject {
                ["filePath"] = filePath,
                ["uniqueId"] = Guid.NewGuid().ToString(),
                ["input"] = new JsonArray(),
                ["output"] = new JsonArray()
            });
            return new ProjectJsonPatchResult { Success = true, Summary = $"Added entry point '{filePath}'." };
        }

        if (existing is null) {
            return Fail($"Entry point '{filePath}' was not found.");
        }

        entryPoints.Remove(existing);
        return new ProjectJsonPatchResult { Success = true, Summary = $"Removed entry point '{filePath}'." };
    }

    private static ProjectJsonPatchResult UpsertOrRemoveDependency(JsonObject root, string? packageId, string? version, bool remove) {
        if (string.IsNullOrWhiteSpace(packageId)) {
            return Fail("packageId is required.");
        }

        var dependencies = root["dependencies"] as JsonObject ?? new JsonObject();
        root["dependencies"] = dependencies;

        if (remove) {
            if (dependencies[packageId] is null) {
                return Fail($"Dependency '{packageId}' was not found.");
            }

            dependencies.Remove(packageId);
            return new ProjectJsonPatchResult { Success = true, Summary = $"Removed dependency '{packageId}'." };
        }

        if (string.IsNullOrWhiteSpace(version)) {
            return Fail("version is required for upsert_dependency.");
        }

        dependencies[packageId] = version;
        return new ProjectJsonPatchResult { Success = true, Summary = $"Set dependency '{packageId}' to '{version}'." };
    }

    private static ProjectJsonPatchResult UpsertOrRemoveFileInfo(JsonObject root, string? filePath, bool remove) {
        if (string.IsNullOrWhiteSpace(filePath)) {
            return Fail("filePath is required.");
        }

        var design = root["designOptions"] as JsonObject ?? new JsonObject();
        root["designOptions"] = design;
        var collection = design["fileInfoCollection"] as JsonArray ?? new JsonArray();
        design["fileInfoCollection"] = collection;

        var existing = FindObject(collection, "fileName", filePath);
        if (remove) {
            if (existing is null) {
                return Fail($"fileInfoCollection entry '{filePath}' was not found.");
            }

            collection.Remove(existing);
            return new ProjectJsonPatchResult { Success = true, Summary = $"Removed fileInfoCollection entry '{filePath}'." };
        }

        if (existing is not null) {
            existing["fileName"] = filePath;
            existing["editingStatus"] ??= "InProgress";
            existing["testCaseType"] ??= "TestCase";
            existing["publishAsTestCase"] ??= true;
            return new ProjectJsonPatchResult { Success = true, Summary = $"Updated fileInfoCollection entry '{filePath}'." };
        }

        collection.Add(new JsonObject {
            ["editingStatus"] = "InProgress",
            ["testCaseId"] = Guid.NewGuid().ToString(),
            ["testCaseType"] = "TestCase",
            ["fileName"] = filePath,
            ["publishAsTestCase"] = true
        });
        return new ProjectJsonPatchResult { Success = true, Summary = $"Added fileInfoCollection entry '{filePath}'." };
    }

    private static ProjectJsonPatchResult SetExceptionHandlerWorkflow(JsonObject root, string? filePath) {
        var runtime = root["runtimeOptions"] as JsonObject ?? new JsonObject();
        root["runtimeOptions"] = runtime;
        if (string.IsNullOrWhiteSpace(filePath)) {
            runtime.Remove("exceptionHandlerWorkflow");
            return new ProjectJsonPatchResult { Success = true, Summary = "Cleared exception handler workflow." };
        }

        runtime["exceptionHandlerWorkflow"] = filePath;
        return new ProjectJsonPatchResult { Success = true, Summary = $"Set exception handler workflow to '{filePath}'." };
    }

    private static ProjectJsonPatchResult SetRuntimeOptionValue(JsonObject root, string? key, string? value) {
        if (string.IsNullOrWhiteSpace(key)) {
            return Fail("key is required.");
        }

        if (ImmutableKeys.Contains(key, StringComparer.OrdinalIgnoreCase)) {
            return Fail($"'{key}' cannot be changed.", ToolErrorCodes.InvalidArgument);
        }

        if (value is null) {
            return Fail("value is required (JSON).");
        }

        JsonNode? node;
        try {
            node = JsonNode.Parse(value);
        } catch (JsonException ex) {
            return Fail($"value is not valid JSON: {ex.Message}");
        }

        var runtime = root["runtimeOptions"] as JsonObject ?? new JsonObject();
        root["runtimeOptions"] = runtime;
        runtime[key] = node;
        return new ProjectJsonPatchResult { Success = true, Summary = $"Set runtimeOptions.{key}." };
    }

    private static JsonObject? FindObject(JsonArray array, string property, string expected) {
        var expectedNormalized = ProjectFilePolicy.NormalizeRelativePath(expected);
        foreach (var item in array) {
            if (item is JsonObject obj
                && obj[property]?.GetValue<string>() is { } actual
                && string.Equals(
                    ProjectFilePolicy.NormalizeRelativePath(actual),
                    expectedNormalized,
                    StringComparison.OrdinalIgnoreCase)) {
                return obj;
            }
        }

        return null;
    }

    private static ProjectJsonPatchResult Fail(string error, string? errorCode = null) => new() {
        Success = false,
        Error = error,
        ErrorCode = errorCode ?? ToolErrorCodes.InvalidArgument
    };
}
