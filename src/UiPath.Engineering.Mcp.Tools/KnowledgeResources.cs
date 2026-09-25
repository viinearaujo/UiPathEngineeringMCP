using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using UiPath.Engineering.Mcp.Core;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.Authoring;
using UiPath.Engineering.Mcp.Core.Configuration;
using UiPath.Engineering.Mcp.Core.Knowledge;
using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Tools;

/// <summary>
/// The activity-metadata and knowledge resources. They remove the two things a caller used to
/// have to do by hand: guess an activity's property surface, and copy the Copilot idiom samples
/// into every target project. Both are URI templates, so a client discovers them through
/// <c>resources/listResourceTemplates</c> rather than <c>resources/list</c>.
/// </summary>
[McpServerResourceType]
public sealed class KnowledgeResources {
    private static readonly JsonSerializerOptions JsonOptions = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly IActivityCatalogResolver _catalogResolver;
    private readonly IFilesystemProvider _filesystem;
    private readonly SkillsOptions _skills;

    public KnowledgeResources(
        IActivityCatalogResolver catalogResolver,
        IFilesystemProvider filesystem,
        IOptions<SkillsOptions> skills) {
        _catalogResolver = catalogResolver;
        _filesystem = filesystem;
        _skills = skills.Value;
    }

    [McpServerResource(UriTemplate = "uipath://activity/{projectPath}/{name}", MimeType = "application/json", Name = "activity-metadata")]
    [Description("The full authoring surface of one activity as JSON — properties with CLR type, direction, required flag, default, allowed values, and the body shape. projectPath may be the literal 'global' to read the built-in fallback catalog.")]
    public async Task<string> GetActivity(string projectPath, string name, CancellationToken cancellationToken = default) {
        try {
            var resolved = ResolveProject(projectPath);
            var catalog = await _catalogResolver.ResolveAsync(resolved, cancellationToken);
            if (!catalog.TryGet(name, out var schema)) {
                return ResourceError(
                    ToolErrorCodes.ActivityNotFound,
                    $"Activity '{name}' is not in the catalog (source: {catalog.Source}).",
                    catalog.Suggest(name) is { } suggestion
                        ? $"Did you mean '{suggestion}'?"
                        : "Call recommend_activities or search_knowledge for a real activity name.");
            }

            var payload = new {
                name = schema.Name,
                renderName = schema.RenderName,
                elementName = schema.ElementName,
                fullTypeName = schema.FullTypeName,
                prefix = schema.Prefix,
                xmlNamespace = schema.XmlNamespace,
                packageId = schema.PackageId,
                packageVersion = schema.PackageVersion,
                isContainer = schema.IsContainer,
                experimental = schema.Experimental,
                propertiesAreComplete = schema.PropertiesAreComplete,
                body = schema.Body is null ? null : new {
                    shape = GetActivityMetadataTool.ToCamel(schema.Body.Shape.ToString()),
                    property = schema.Body.Property,
                    delegateType = schema.Body.DelegateType,
                    iteratorName = schema.Body.IteratorName
                },
                properties = schema.Properties.Select(p => new {
                    name = p.Name,
                    kind = GetActivityMetadataTool.ToCamel(p.Kind.ToString()),
                    required = p.Required,
                    clrType = p.ClrType,
                    direction = p.Direction is { } direction ? GetActivityMetadataTool.ToCamel(direction.ToString()) : null,
                    defaultValue = p.Default,
                    allowedValues = p.AllowedValues,
                    isContentProperty = p.IsContentProperty
                }).ToList()
            };

            return JsonSerializer.Serialize(payload, JsonOptions);
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            return ResourceFailure("activity-metadata", "Activity metadata read failed.", ex);
        }
    }

    [McpServerResource(UriTemplate = "uipath://idioms/{name}", MimeType = "text/markdown", Name = "copilot-idiom")]
    [Description("A shipped Copilot idiom sample (coded workflow, REFramework shell, coded test). Read it instead of copying the sample into the target project.")]
    public string GetIdiom(string name) {
        try {
            var root = KnowledgePaths.ResolveCopilotIdiomsRoot(_skills);
            if (root is null) {
                return ResourceError(
                    ToolErrorCodes.SkillsRootMissing,
                    "The Copilot idiom samples were not found.",
                    "Set Skills:CopilotIdiomsRoot in appsettings.json to the folder holding the idiom markdown (default 'docs/copilot-idioms').");
            }

            var fileName = Path.GetFileName(name.Trim());
            if (fileName.Length == 0
                || (!fileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase) && !fileName.Contains('.', StringComparison.Ordinal))) {
                fileName += ".md";
            }

            var target = Path.GetFullPath(Path.Combine(root, fileName));
            // Confinement: the resolved path must stay inside the idioms root. Path.GetFileName
            // already strips separators, so this is belt-and-braces.
            if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) {
                return ResourceError(ToolErrorCodes.SkillPathRejected, $"'{name}' escapes the idioms folder.", "Pass a file name, e.g. 'coded-workflow-try-log.md'.");
            }

            if (!File.Exists(target)) {
                var available = string.Join(", ", Directory.EnumerateFiles(root, "*.md")
                    .Select(Path.GetFileName)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
                return ResourceError(
                    ToolErrorCodes.SkillFileNotFound,
                    $"'{fileName}' does not exist in the idioms folder.",
                    $"Available idioms: {available}.");
            }

            var (redacted, _) = SecretRedactor.Redact(File.ReadAllText(target));
            return redacted;
        } catch (Exception ex) {
            return ResourceFailure("copilot-idiom", "Idiom read failed.", ex);
        }
    }

    // The literal "global" selects the built-in fallback catalog, which is what a caller outside
    // a specific project wants. Any other value must be an allowed project directory.
    private string? ResolveProject(string projectPath) {
        if (string.IsNullOrWhiteSpace(projectPath)
            || string.Equals(projectPath, GlobalProject, StringComparison.OrdinalIgnoreCase)) {
            return null;
        }

        return _filesystem.IsPathAllowed(projectPath) && _filesystem.FindProjectJson(projectPath) is not null
            ? projectPath
            : null;
    }

    internal const string GlobalProject = "global";

    private static string ResourceFailure(string resource, string clientMessage, Exception ex) =>
        // A resource read has no logger seam; the message stays generic and the exception text
        // never reaches the client.
        ResourceError(ToolErrorCodes.OperationFailed, clientMessage, $"Retry the resource read; the {resource} source may be unavailable.");

    private static string ResourceError(string errorCode, string message, string fixHint) =>
        JsonSerializer.Serialize(new {
            error = new { errorCode, message, fixHint }
        }, JsonOptions);
}