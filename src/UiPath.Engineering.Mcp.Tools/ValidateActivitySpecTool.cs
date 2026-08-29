using System.ComponentModel;
using System.Diagnostics;
using ModelContextProtocol.Server;
using UiPath.Engineering.Mcp.Core;
using UiPath.Engineering.Mcp.Core.Authoring;
using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Tools;

[McpServerToolType]
public sealed class ValidateActivitySpecTool {
    [McpServerTool(UseStructuredContent = true), Description("Validates a JSON activity spec against the UiPath activity catalog without reading or writing any files. Returns every violation as a structured error (errorCode, message, fixHint), or the list of catalog activities the spec uses. Use this as a dry-run before authoring or editing workflows. Spec shape: { name, properties, children, variables (root only), catches (TryCatch only) }. An If spec has no Else branch — Children is the Then branch.")]
    public ToolResult ValidateActivitySpec(
        [Description("JSON activity spec to validate (no files are read or written).")] string specJson) {

        var sw = Stopwatch.StartNew();

        if (!SpecJson.TryDeserialize(specJson, out var spec, out var deserializeError)) {
            return ToolResults.Failure(deserializeError!, sw);
        }

        var errors = SpecValidator.Validate(spec!);

        if (errors.Count > 0) {
            return ToolResults.Failure($"The activity spec has {errors.Count} violation(s).", errors, sw);
        }

        // Renderability proof: a valid spec must render. Surface XAML_RENDER_FAILED
        // if the builder ever fails on a valid spec.
        var build = XamlBuilder.RenderFragment(spec!);
        if (!build.Success) {
            return ToolResults.Failure("The activity spec validated but failed to render as XAML.", build.Errors, sw);
        }

        var activitiesUsed = new List<string>();
        CollectActivities(spec!, activitiesUsed);

        var warnings = ExperimentalWarnings(activitiesUsed, name =>
            ActivityCatalog.TryGet(name, out var schema) && schema.Experimental);

        return ToolResults.Ok(
            $"The activity spec is valid; it uses {activitiesUsed.Count} distinct activity type(s).",
            new {
                valid = true,
                activitiesUsed,
                warnings
            }, sw, warnings);
    }

    // One "experimental: ..." warning per used activity the lookup reports as
    // experimental. Internal + injectable so tests can exercise the path without
    // an experimental entry in the shipped catalog.
    internal static List<string> ExperimentalWarnings(IReadOnlyList<string> activitiesUsed, Func<string, bool> isExperimental) =>
        activitiesUsed
            .Where(isExperimental)
            .Select(name => $"experimental: \"{name}\" is an experimental activity; its schema may change.")
            .ToList();

    internal static void CollectActivities(ActivitySpec spec, List<string> used) {
        if (ActivityCatalog.TryGet(spec.Name, out var schema) && !used.Contains(schema.Name)) {
            used.Add(schema.Name);
        }
        foreach (var child in spec.Children ?? []) {
            CollectActivities(child, used);
        }
        foreach (var catchSpec in spec.Catches ?? []) {
            foreach (var child in catchSpec.Children ?? []) {
                CollectActivities(child, used);
            }
        }
    }
}
