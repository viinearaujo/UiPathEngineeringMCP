using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Core.Parsing;

namespace UiPath.Engineering.Mcp.Core.Authoring;

// The expression language a XAML workflow is written in. UiPath fixes this at
// project creation ('create_project' / 'uip rpa init') and it is immutable
// afterwards, so every expression in every .xaml file of a project must use it.
public enum ExpressionLanguage {
    VisualBasic,
    CSharp
}

/// <summary>
/// Project-level settings that decide how a spec is rendered as XAML: the
/// expression language (which binding form expressions must take) and whether
/// the project targets legacy .NET Framework (which changes the assembly half
/// of every <c>clr-namespace:</c> alias).
/// </summary>
/// <remarks>
/// <see cref="Default"/> is VisualBasic on a modern target, which is what the
/// builder produced before these settings existed — so callers with no project
/// to read (a bare <c>validate_activity_spec</c> without <c>projectPath</c>)
/// keep the historical behavior.
/// </remarks>
public sealed record ProjectXamlSettings {
    public const string ModernCoreAssembly = "System.Private.CoreLib";
    public const string LegacyCoreAssembly = "mscorlib";

    public ExpressionLanguage ExpressionLanguage { get; init; } = ExpressionLanguage.VisualBasic;

    /// <summary>True when the project targets .NET Framework 4.6.1 ("Legacy").</summary>
    public bool IsLegacyTargetFramework { get; init; }

    public static ProjectXamlSettings Default { get; } = new();

    public bool IsCSharp => ExpressionLanguage == ExpressionLanguage.CSharp;

    /// <summary>
    /// Assembly name for the <c>System</c>, <c>System.Collections.Generic</c> and
    /// <c>System.Collections.ObjectModel</c> aliases: <c>mscorlib</c> on legacy
    /// .NET Framework projects, <c>System.Private.CoreLib</c> on modern ones.
    /// </summary>
    public string CoreAssembly => IsLegacyTargetFramework ? LegacyCoreAssembly : ModernCoreAssembly;

    /// <summary>
    /// Reads the settings off a parsed <c>project.json</c> model. A model with no
    /// <c>expressionLanguage</c> key is treated as VisualBasic (UiPath's default
    /// and the only language the bracket shorthand belongs to).
    /// </summary>
    public static ProjectXamlSettings From(UiPathProjectModel? model) => model is null
        ? Default
        : new ProjectXamlSettings {
            ExpressionLanguage = ParseExpressionLanguage(model.ExpressionLanguage),
            IsLegacyTargetFramework = IsLegacyFramework(model.TargetFramework)
        };

    public static ExpressionLanguage ParseExpressionLanguage(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && (value.Equals("CSharp", StringComparison.OrdinalIgnoreCase)
            || value.Equals("C#", StringComparison.OrdinalIgnoreCase))
            ? ExpressionLanguage.CSharp
            : ExpressionLanguage.VisualBasic;

    /// <summary>
    /// Resolves the settings for a project through the cached project-model
    /// builder. With no builder, no project path, or a project that cannot be
    /// read, this returns <see cref="Default"/> so the no-project behavior is
    /// unchanged: expression form is never guessed at, it falls back to the VB
    /// shorthand that has always been emitted.
    /// </summary>
    public static async Task<ProjectXamlSettings> ResolveAsync(
        IProjectModelBuilder? builder, string? projectPath, CancellationToken cancellationToken = default) {
        if (builder is null || string.IsNullOrWhiteSpace(projectPath)) {
            return Default;
        }

        try {
            return From(await builder.BuildAsync(projectPath, cancellationToken));
        } catch (Exception ex) when (ex is FileNotFoundException or IOException
            or UnauthorizedAccessException or System.Text.Json.JsonException) {
            // An unreadable project.json must not turn authoring into a hard
            // failure; the caller already guards on project.json elsewhere.
            return Default;
        }
    }

    // 'project.json' carries "Windows" / "Portable" / "Legacy". Very old projects
    // omit the key entirely; those are left on the modern assembly because the
    // builder cannot tell them apart from a model that simply was not loaded.
    internal static bool IsLegacyFramework(string? targetFramework) {
        if (string.IsNullOrWhiteSpace(targetFramework)) {
            return false;
        }

        var value = targetFramework.Trim();
        return value.Equals("Legacy", StringComparison.OrdinalIgnoreCase)
            || value.Contains(".NETFramework", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("net4", StringComparison.OrdinalIgnoreCase);
    }
}
