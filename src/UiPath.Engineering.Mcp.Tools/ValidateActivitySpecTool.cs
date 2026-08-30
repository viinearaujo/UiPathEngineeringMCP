using System.ComponentModel;
using System.Diagnostics;
using ModelContextProtocol.Server;
using UiPath.Engineering.Mcp.Core;
using UiPath.Engineering.Mcp.Core.Authoring;
using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Tools;

[McpServerToolType]
public sealed class ValidateActivitySpecTool {
    private readonly IActivityCatalogResolver _catalogResolver;

    public ValidateActivitySpecTool(IActivityCatalogResolver catalogResolver) {
        _catalogResolver = catalogResolver;
    }

    [McpServerTool(UseStructuredContent = true), Description("Validates a JSON activity spec against the UiPath activity catalog without reading or writing any files. Pass projectPath to use package-native schemas for that project. Returns every violation as a structured error (errorCode, message, fixHint), or the list of catalog activities the spec uses. Use this as a dry-run before authoring or editing workflows. Spec shape: { name, properties, children, variables (root only), catches (TryCatch only), else (If), cases/default (Switch), arguments (InvokeWorkflowFile) }. If Children is the Then branch; else is the Else branch.")]
    public async Task<ToolResult> ValidateActivitySpec(
        [Description("JSON activity spec to validate (no files are read or written).")] string specJson,
        [Description("Optional absolute path to the UiPath project directory. When set, validation uses that project's package catalog; otherwise the built-in fallback catalog.")] string? projectPath = null,
        CancellationToken cancellationToken = default) {

        var sw = Stopwatch.StartNew();

        if (!SpecJson.TryDeserialize(specJson, out var spec, out var deserializeError)) {
            return ToolResults.Failure(deserializeError!, sw);
        }

        var catalog = await _catalogResolver.ResolveAsync(projectPath, cancellationToken);
        var errors = SpecValidator.Validate(spec!, catalog);

        if (errors.Count > 0) {
            return ToolResults.Failure($"The activity spec has {errors.Count} violation(s).", errors, sw);
        }

        // Renderability proof: a valid spec must render. Surface XAML_RENDER_FAILED
        // if the builder ever fails on a valid spec.
        var build = XamlBuilder.RenderFragment(spec!, catalog);
        if (!build.Success) {
            return ToolResults.Failure("The activity spec validated but failed to render as XAML.", build.Errors, sw);
        }

        var activitiesUsed = new List<string>();
        CollectActivities(spec!, activitiesUsed, catalog);

        var warnings = ExperimentalWarnings(activitiesUsed, name =>
            catalog.TryGet(name, out var schema) && schema.Experimental);

        return ToolResults.Ok(
            $"The activity spec is valid; it uses {activitiesUsed.Count} distinct activity type(s).",
            new {
                valid = true,
                source = catalog.Source,
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

    internal static void CollectActivities(ActivitySpec spec, List<string> used, IActivityCatalog? catalog = null) {
        catalog ??= ActivityCatalog.Fallback;
        if (catalog.TryGet(spec.Name, out var schema) && !used.Contains(schema.Name)) {
            used.Add(schema.Name);
        }
        foreach (var child in spec.Children ?? []) {
            CollectActivities(child, used, catalog);
        }
        foreach (var child in spec.Else ?? []) {
            CollectActivities(child, used, catalog);
        }
        foreach (var child in spec.Default ?? []) {
            CollectActivities(child, used, catalog);
        }
        foreach (var switchCase in spec.Cases ?? []) {
            foreach (var child in switchCase.Children ?? []) {
                CollectActivities(child, used, catalog);
            }
        }
        foreach (var catchSpec in spec.Catches ?? []) {
            foreach (var child in catchSpec.Children ?? []) {
                CollectActivities(child, used, catalog);
            }
        }
    }
}
