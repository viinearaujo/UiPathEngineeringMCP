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
/// </summary>
public sealed class CSharpContextBuilder : ICSharpContextBuilder {
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
        foreach (var file in csFiles) {
            cancellationToken.ThrowIfCancellationRequested();
            try {
                var text = _filesystem.ReadAllText(file);
                trees.Add(CSharpSyntaxTree.ParseText(text, parseOptions, path: file, cancellationToken: cancellationToken));
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException) {
                warnings.Add($"Skipped unreadable C# file '{file}': {ex.Message}");
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
            HasCSharpFiles = csFiles.Count > 0
        });
    }
}
