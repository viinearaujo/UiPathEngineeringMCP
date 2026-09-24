using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.Parsing;

namespace UiPath.Engineering.Mcp.Core.CodeAnalysis;

/// <summary>
/// Builds a <see cref="CSharpAnalysisContext"/> for a UiPath project: parses every
/// .cs file, resolves references from project.json via <see cref="NuGetReferenceResolver"/>,
/// and assembles the <see cref="CSharpCompilation"/>. Unreadable files and unloadable
/// assemblies are skipped with warnings instead of failing the whole build.
/// The compilation inputs are the authored sources returned by
/// <see cref="IFilesystemProvider.FindCSharpFiles"/> unioned with the Studio-generated
/// coded-workflow sources under <c>.local/.codedworkflows/</c> — see
/// <see cref="FindGeneratedCodedFiles"/>.
/// </summary>
public sealed class CSharpContextBuilder : ICSharpContextBuilder {
    /// <summary>
    /// Studio-generated coded-workflow support folder, relative to the project root. It
    /// carries the Object Repository descriptor surface
    /// (<c>Descriptors.&lt;App&gt;.&lt;Screen&gt;.&lt;Element&gt;</c>), the generated
    /// <c>CodedWorkflow</c> service accessors and the workflow-runner support types.
    /// </summary>
    internal const string GeneratedCodedWorkflowsFolder = ".local/.codedworkflows";

    private readonly IFilesystemProvider _filesystem;
    private readonly NuGetReferenceResolver _resolver;

    public CSharpContextBuilder(IFilesystemProvider filesystem, NuGetReferenceResolver resolver) {
        _filesystem = filesystem;
        _resolver = resolver;
    }

    public Task<CSharpAnalysisContext> BuildAsync(string projectPath, CancellationToken cancellationToken = default) {
        var projectJsonPath = _filesystem.FindProjectJson(projectPath)
            ?? throw new FileNotFoundException("project.json not found.", Path.Combine(projectPath, "project.json"));
        var model = new ProjectJsonParser(_filesystem).Parse(projectJsonPath, projectPath);

        var warnings = new List<string>();
        var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest);
        var csFiles = _filesystem.FindCSharpFiles(projectPath);
        var trees = new List<SyntaxTree>();
        var authoredNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in csFiles) {
            cancellationToken.ThrowIfCancellationRequested();
            authoredNames.Add(Path.GetFileName(file));
            try {
                var text = _filesystem.ReadAllText(file);
                trees.Add(CSharpSyntaxTree.ParseText(text, parseOptions, path: file, cancellationToken: cancellationToken));
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException) {
                warnings.Add($"Skipped unreadable C# file '{file}': {ex.Message}");
            }
        }

        // Generated sources are read with direct IO: `.local` is deliberately hidden from
        // IFilesystemProvider discovery so generated code never reaches the search, project
        // model, gap-analysis or authoring surfaces. It is a compile input only.
        foreach (var file in FindGeneratedCodedFiles(projectPath)) {
            cancellationToken.ThrowIfCancellationRequested();
            if (!authoredNames.Add(Path.GetFileName(file))) {
                // The project already compiles a file of this name. Prefer the authored
                // copy so a stale generated duplicate cannot introduce a CS0101
                // duplicate-definition error — same rationale as
                // NuGetReferenceResolver.DeduplicateByFileName for CS1703.
                continue;
            }

            try {
                var text = File.ReadAllText(file);
                trees.Add(CSharpSyntaxTree.ParseText(text, parseOptions, path: file, cancellationToken: cancellationToken));
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException) {
                warnings.Add($"Skipped unreadable generated C# file '{file}': {ex.Message}");
            }
        }

        var resolution = _resolver.Resolve(model.Packages, model.TargetFramework);
        var references = new List<MetadataReference>();
        foreach (var path in resolution.AssemblyPaths) {
            try {
                references.Add(MetadataReference.CreateFromFile(path));
            } catch (Exception ex) when (ex is IOException or BadImageFormatException or FileNotFoundException) {
                warnings.Add($"Skipped unloadable assembly '{path}': {ex.Message}");
            }
        }

        var compilation = CSharpCompilation.Create(
            assemblyName: $"analysis-{model.ProjectName}",
            syntaxTrees: trees,
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var mode = model.Packages.Count > 0 && !resolution.PackagesFolderFound
            ? CSharpAnalysisMode.SyntaxOnly
            : resolution.UnresolvedDependencies.Count > 0 || !resolution.FrameworkResolved
                ? CSharpAnalysisMode.Partial
                : CSharpAnalysisMode.Full;

        return Task.FromResult(new CSharpAnalysisContext {
            Compilation = compilation,
            Mode = mode,
            UnresolvedReferences = resolution.UnresolvedDependencies,
            Warnings = warnings,
            // Authored sources only: a project whose sole .cs file is generated has no
            // coded workflow, and the "no C# files" note must keep saying so.
            HasCSharpFiles = csFiles.Count > 0
        });
    }

    /// <summary>
    /// Discovers the Studio-generated coded-workflow sources under
    /// <c>{projectPath}/.local/.codedworkflows/</c> — above all <c>ObjectRepository.cs</c>,
    /// which declares the <c>Descriptors.&lt;App&gt;.&lt;Screen&gt;.&lt;Element&gt;</c>
    /// strongly-typed accessors that coded UI automation calls into. Without them every
    /// descriptor reference reports bogus <c>CS0103</c>/<c>CS0246</c>.
    /// Returns an empty list when the folder is absent — no Object Repository captured yet,
    /// or `.local` never created — so compilation behaviour is unchanged and silent.
    /// </summary>
    /// <remarks>
    /// Direct <see cref="System.IO"/> rather than <see cref="IFilesystemProvider"/> by
    /// design: <c>.local</c> is on the provider's ignore list, which is what keeps
    /// generated code out of <c>search_codebase</c>, <c>analyze_project</c>, the project
    /// model, the gap analyzer and <c>edit_workflow_file</c>. It is a compile input only.
    /// The sandbox still holds because the folder is resolved through
    /// <see cref="PathPolicy.TryResolveProjectRelative"/>, which canonicalizes (following
    /// reparse points) and rejects anything not landing inside the already-allowed project
    /// root. Same rationale as <see cref="NuGetReferenceResolver"/>'s direct IO.
    /// </remarks>
    internal static IReadOnlyList<string> FindGeneratedCodedFiles(string projectPath) {
        if (!PathPolicy.TryResolveProjectRelative(projectPath, GeneratedCodedWorkflowsFolder, out var folder)
            || !Directory.Exists(folder)) {
            return [];
        }

        string[] files;
        try {
            files = Directory.GetFiles(folder, "*.cs", SearchOption.TopDirectoryOnly);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return [];
        }

        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        return files;
    }
}
