using System.ComponentModel;
using ModelContextProtocol.Server;

namespace UiPath.Engineering.Mcp.Tools;

/// <summary>
/// Authoring-guide resources. The activity-spec grammar used to live in tool descriptions;
/// keep it here so validate_activity_spec / build_workflow / insert_activities stay short.
/// The grammar text is docs/authoring/activity-spec.md, embedded at build time; the Copilot
/// Studio skill zips ship the same file, so edit the markdown, not this class.
/// </summary>
[McpServerResourceType]
public sealed class AuthoringResources {
    public const string ActivitySpecUri = "uipath://authoring/activity-spec";
    private const string ActivitySpecResourceName = "UiPath.Engineering.Mcp.Tools.activity-spec.md";

    private static readonly Lazy<string> ActivitySpecGuide = new(LoadActivitySpecGuide);

    [McpServerResource(UriTemplate = ActivitySpecUri, MimeType = "text/markdown", Name = "activity-spec")]
    [Description("JSON activity-spec grammar: node shape, expression-language rules, and container rules for validate_activity_spec, build_workflow, and insert_activities.")]
    public string GetActivitySpecGuide() => ActivitySpecGuide.Value;

    private static string LoadActivitySpecGuide() {
        var assembly = typeof(AuthoringResources).Assembly;
        using var stream = assembly.GetManifestResourceStream(ActivitySpecResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{ActivitySpecResourceName}' is missing from {assembly.GetName().Name}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
