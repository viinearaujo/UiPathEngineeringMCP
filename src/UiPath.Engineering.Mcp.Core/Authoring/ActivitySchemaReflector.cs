using System.Reflection;
using UiPath.Engineering.Mcp.Core.CodeAnalysis;
using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Core.Authoring;

/// <summary>
/// Reads an activity type's complete settable surface from the assemblies in the
/// project's NuGet packages folder, using <see cref="MetadataLoadContext"/> so the
/// assemblies are inspected without being loaded into the server's process (and
/// without their static state running).
/// </summary>
/// <remarks>
/// The catalog's starter-based surface (<see cref="DefaultXamlParser"/> over
/// <c>activities get-default-xaml</c>) carries only the properties whose value
/// differs from the CLR type default, so required-ness is not authoritative for a
/// discovered activity — a required property left at its default is invisible.
/// Reflection supplies the full surface: every public argument property declared
/// from the concrete type down to (but excluding) the framework
/// <c>System.Activities</c> base, with its CLR type, argument direction, allowed
/// default, and required flag, plus the content property and the body slot the
/// activity's <c>ContentProperty</c> attribute names.
///
/// Every failure path returns null rather than throwing: a package that is not
/// installed, an assembly that cannot be read, or a type that is not a concrete
/// class all fall back to the starter-derived surface, which is strictly better
/// than failing the catalog resolution.
/// </remarks>
public static class ActivitySchemaReflector {
    // Namespaces whose members are activity infrastructure, never a spec property.
    // The walk stops at the first base type in one of these, so Activity.Id,
    // Activity.CacheId and Activity.Constraints never surface.
    private static readonly string[] FrameworkTypePrefixes = ["System", "Microsoft"];

    private static readonly HashSet<string> ArgumentWrapperTypes = new(StringComparer.Ordinal) {
        "System.Activities.InArgument`1",
        "System.Activities.OutArgument`1",
        "System.Activities.InOutArgument`1"
    };

    /// <summary>
    /// The complete property surface of <paramref name="fullTypeName"/>, or null
    /// when the type cannot be resolved or read. Framework references and package
    /// assemblies are resolved through <see cref="NuGetReferenceResolver"/>, the
    /// same probing Roslyn uses.
    /// </summary>
    public static IReadOnlyList<PropertySchema>? TryReadProperties(
        string packagesFolder,
        IReadOnlyList<PackageModel> packages,
        string? targetFramework,
        string? fullTypeName) {
        if (string.IsNullOrWhiteSpace(fullTypeName) || packagesFolder.Length == 0) {
            return null;
        }

        List<string> assemblyPaths;
        try {
            assemblyPaths = new NuGetReferenceResolver(packagesFolder)
                .Resolve(packages, targetFramework)
                .AssemblyPaths
                .ToList();
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) {
            return null;
        }

        return TryReadProperties(assemblyPaths, fullTypeName);
    }

    /// <summary>
    /// The complete property surface of <paramref name="fullTypeName"/> read from an
    /// explicit assembly set. Separate from the package probing so tests can supply
    /// their own assemblies. <paramref name="diagnostics"/> collects why a read
    /// failed; it is populated in tests and left null in production.
    /// </summary>
    internal static IReadOnlyList<PropertySchema>? TryReadProperties(
        IReadOnlyList<string> assemblyPaths, string? fullTypeName, List<string>? diagnostics = null) {
        var typeName = NormalizeTypeName(fullTypeName);
        if (typeName.Length == 0 || assemblyPaths.Count == 0 || IsFrameworkTypeName(typeName)) {
            diagnostics?.Add($"rejected: type='{typeName}' paths={assemblyPaths.Count}");
            return null;
        }

        // MetadataLoadContext needs a core assembly (System.Object, primitives) before
        // it can resolve anything. A framework ref-pack / package list may not carry
        // one, so the server's own runtime core is added unless the caller supplied it.
        var paths = WithCoreAssembly(assemblyPaths);
        try {
            using var context = new MetadataLoadContext(
                new PathAssemblyResolver(paths), coreAssemblyName: "System.Private.CoreLib");
            // GetAssemblies() returns only assemblies loaded on demand, so every path
            // is loaded explicitly; the activity we want is in one of them.
            var assemblies = new List<Assembly>();
            foreach (var path in paths) {
                try {
                    assemblies.Add(context.LoadFromAssemblyPath(path));
                } catch (Exception ex) when (ex is FileNotFoundException
                    or FileLoadException
                    or BadImageFormatException
                    or IOException
                    or NotSupportedException
                    or ArgumentException) {
                    // A path that will not load (native image, duplicate, wrong TFM) is
                    // simply not a candidate; resolution continues over the rest.
                }
            }

            var type = ResolveType(assemblies, typeName);
            if (type is null) {
                diagnostics?.Add($"type '{typeName}' not found in {assemblies.Count} loaded assemblies");
                return null;
            }

            var baseName = "unknown";
            try {
                baseName = type.BaseType?.FullName ?? "none";
            } catch (Exception ex) when (ex is FileNotFoundException or TypeLoadException or NotSupportedException or ReflectionTypeLoadException) {
            }

            diagnostics?.Add($"resolved '{typeName}' -> base '{baseName}'");
            var surface = ReadSurface(type, diagnostics);
            diagnostics?.Add($"surface count={surface.Count}");
            return surface;
        } catch (Exception ex) when (ex is FileNotFoundException
            or BadImageFormatException
            or IOException
            or NotSupportedException
            or ArgumentException
            or TypeLoadException
            or InvalidOperationException) {
            diagnostics?.Add($"exception {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static List<string> WithCoreAssembly(IReadOnlyList<string> assemblyPaths) {
        var paths = new List<string>(assemblyPaths);
        var coreLocation = typeof(object).Assembly.Location;
        if (coreLocation.Length > 0
            && !paths.Any(p => Path.GetFileName(p).Equals("System.Private.CoreLib.dll", StringComparison.OrdinalIgnoreCase))) {
            paths.Add(coreLocation);
        }

        return paths;
    }

    private static Type? ResolveType(IReadOnlyList<Assembly> assemblies, string typeName) {
        foreach (var assembly in assemblies) {
            foreach (var type in SafeTypes(assembly)) {
                if (string.Equals(SafeFullName(type), typeName, StringComparison.Ordinal)) {
                    return type;
                }

                // A generic arity mismatch (the caller wrote Assign`1 but the assembly
                // holds Assign`2, or vice versa) still resolves by the open
                // definition's bare name.
                if (typeName.Contains('`')) {
                    var bare = typeName[..typeName.IndexOf('`')];
                    if (string.Equals(SafeFullName(type), bare, StringComparison.Ordinal)) {
                        return type;
                    }
                }
            }
        }

        return null;
    }

    // GetTypes() throws ReflectionTypeLoadException when ANY type in the assembly is
    // unresolvable (a reference the metadata context cannot see), even though the
    // rest load fine. The exception carries the partial list, which is what matters
    // here: the activity type we want is among the resolvable ones.
    private static IEnumerable<Type> SafeTypes(Assembly assembly) {
        try {
            return assembly.GetTypes();
        } catch (ReflectionTypeLoadException ex) {
            return ex.Types.Where(t => t is not null)!;
        } catch (Exception ex) when (ex is FileNotFoundException or TypeLoadException or BadImageFormatException) {
            return [];
        }
    }

    private static string? SafeFullName(Type type) {
        try {
            return type.FullName;
        } catch (Exception ex) when (ex is FileNotFoundException or TypeLoadException or NotSupportedException or ReflectionTypeLoadException) {
            return null;
        }
    }

    /// <summary>
    /// Strips an assembly-qualified name and normalizes nested-type separators so a
    /// <c>FullTypeName</c> from any discovery source resolves. A generic arity
    /// marker is kept: the open definition (<c>Ns.Type`1</c>) is what the metadata
    /// context contains, and its argument properties reflect the same way.
    /// </summary>
    internal static string NormalizeTypeName(string? fullTypeName) {
        if (string.IsNullOrWhiteSpace(fullTypeName)) {
            return string.Empty;
        }

        var name = fullTypeName.Trim();
        var comma = name.IndexOf(',');
        if (comma > 0) {
            name = name[..comma].Trim();
        }

        return name.Replace('+', '.');
    }

    // A framework activity (System.Activities.Statements.Assign, …) carries no
    // package-specific argument surface: its properties come from the framework
    // and are never a spec property, so reflection would return nothing useful.
    internal static bool IsFrameworkTypeName(string typeName) {
        foreach (var prefix in FrameworkTypePrefixes) {
            if (typeName.StartsWith(prefix + ".", StringComparison.Ordinal)) {
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<PropertySchema> ReadSurface(Type type, List<string>? diagnostics = null) {
        var contentProperty = ContentPropertyName(type);
        var properties = new List<PropertySchema> { new("DisplayName", false, PropertyKind.Literal, ClrType: "String") };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "DisplayName" };
        var depth = 0;

        for (var current = type; current is not null; current = TryBaseType(current)) {
            if (++depth > 12 || !IsReflectedType(current)) {
                break;
            }

            foreach (var property in DeclaredProperties(current)) {
                try {
                    if (!seen.Add(property.Name) || !IsSettable(property) || IsBodyProperty(property)) {
                        continue;
                    }

                    if (ToSchema(property, contentProperty) is { } schema) {
                        properties.Add(schema);
                    }
                } catch (ReflectionTypeLoadException) {
                    diagnostics?.Add($"skipped property {current.Name}.{property.Name}: ReflectionTypeLoadException");
                } catch (Exception ex) when (ex is FileNotFoundException or TypeLoadException or NotSupportedException or ReflectionTypeLoadException) {
                    diagnostics?.Add($"skipped property {current.Name}.{property.Name}: {ex.GetType().Name}");
                }
            }
        }

        return properties.Count == 0 ? [] : properties;
    }

    private static Type? TryBaseType(Type type) {
        try {
            return type.BaseType;
        } catch (Exception ex) when (ex is FileNotFoundException or TypeLoadException or NotSupportedException or ReflectionTypeLoadException) {
            return null;
        }
    }

    private static PropertyInfo[] DeclaredProperties(Type type) {
        try {
            return type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        } catch (ReflectionTypeLoadException) {
            return [];
        } catch (Exception ex) when (ex is FileNotFoundException or TypeLoadException or NotSupportedException or ReflectionTypeLoadException) {
            return [];
        }
    }

    // The walk stops at the framework Activity base: those members are activity
    // identity (Id, CacheId, Constraints), never a spec property.
    private static bool IsReflectedType(Type type) {
        var ns = type.Namespace;
        if (string.IsNullOrEmpty(ns)) {
            return false;
        }

        foreach (var prefix in FrameworkTypePrefixes) {
            if (ns.StartsWith(prefix, StringComparison.Ordinal)) {
                return false;
            }
        }

        return true;
    }

    private static bool IsSettable(PropertyInfo property) {
        try {
            return property.CanWrite && property.GetSetMethod() is not null;
        } catch (Exception ex) when (ex is FileNotFoundException or TypeLoadException) {
            return false;
        }
    }

    // A property whose type is an Activity / ActivityAction / DelegateArgument is a
    // body slot or a callback, not a settable argument the spec renders as an
    // attribute.
    private static bool IsBodyProperty(PropertyInfo property) {
        if (!IsFrameworkOrActivityType(property.PropertyType, out var fullName)) {
            return false;
        }

        return fullName.StartsWith("System.Activities.Activity", StringComparison.Ordinal)
            || fullName.StartsWith("System.Activities.ActivityAction", StringComparison.Ordinal)
            || fullName.StartsWith("System.Activities.ActivityDelegate", StringComparison.Ordinal)
            || fullName.StartsWith("System.Activities.DelegateArgument", StringComparison.Ordinal)
            || fullName.StartsWith("System.Activities.Variable", StringComparison.Ordinal);
    }

    private static PropertySchema? ToSchema(PropertyInfo property, string? contentProperty) {
        var propertyType = property.PropertyType;
        string? fullName = null;
        try {
            fullName = propertyType.FullName;
        } catch (Exception ex) when (ex is FileNotFoundException or TypeLoadException) {
            return null;
        }

        var kind = PropertyKind.Literal;
        ArgumentDirection? direction = null;
        string? clrType = null;

        if (fullName is not null && fullName.StartsWith("System.Activities.", StringComparison.Ordinal)) {
            var genericMarker = fullName.IndexOf("`1[[", StringComparison.Ordinal);
            var definition = genericMarker < 0 ? fullName : fullName[..(genericMarker + 2)];
            if (ArgumentWrapperTypes.Contains(definition)) {
                kind = PropertyKind.Expression;
                direction = definition.Contains("InOutArgument", StringComparison.Ordinal) ? ArgumentDirection.InOut
                    : definition.Contains("OutArgument", StringComparison.Ordinal) ? ArgumentDirection.Out
                    : ArgumentDirection.In;
                clrType = InnerTypeName(propertyType);
            } else {
                // Another framework argument shape (e.g. Variable<T>): not a spec
                // attribute the builder can render.
                return null;
            }
        } else {
            clrType = propertyType.Name;
        }

        var (required, defaultValue) = ReadAttributes(property);
        if (property.Name.Equals("TypeArguments", StringComparison.OrdinalIgnoreCase)) {
            kind = PropertyKind.TypeArgument;
        }

        return new PropertySchema(
            property.Name,
            required,
            kind,
            ClrType: clrType,
            Default: defaultValue,
            Direction: direction,
            IsContentProperty: string.Equals(property.Name, contentProperty, StringComparison.Ordinal));
    }

    private static string? InnerTypeName(Type type) {
        try {
            var arguments = type.IsGenericType ? type.GetGenericArguments() : null;
            return arguments is { Length: 1 } ? arguments[0].Name : null;
        } catch (Exception ex) when (ex is FileNotFoundException or TypeLoadException) {
            return null;
        }
    }

    private static (bool Required, string? Default) ReadAttributes(PropertyInfo property) {
        var required = false;
        string? defaultValue = null;
        try {
            foreach (var attribute in property.GetCustomAttributesData()) {
                var name = attribute.AttributeType.FullName;
                if (name == "System.Activities.RequiredArgumentAttribute") {
                    required = true;
                } else if (name == "System.ComponentModel.DefaultValueAttribute"
                    && attribute.ConstructorArguments.Count == 1) {
                    defaultValue = attribute.ConstructorArguments[0].Value?.ToString();
                }
            }
        } catch (Exception ex) when (ex is FileNotFoundException or TypeLoadException or InvalidOperationException or ReflectionTypeLoadException) {
            // Attributes are a bonus; a partial surface still beats none.
        }

        return (required, defaultValue);
    }

    private static string? ContentPropertyName(Type type) {
        try {
            foreach (var attribute in type.GetCustomAttributesData()) {
                if (attribute.AttributeType.FullName == "System.Windows.Markup.ContentPropertyAttribute"
                    && attribute.ConstructorArguments.Count == 1
                    && attribute.ConstructorArguments[0].Value is string name) {
                    return name;
                }
            }
        } catch (Exception ex) when (ex is FileNotFoundException or TypeLoadException or InvalidOperationException or ReflectionTypeLoadException) {
            return null;
        }

        return null;
    }

    private static bool IsFrameworkOrActivityType(Type type, out string fullName) {
        fullName = string.Empty;
        try {
            fullName = type.FullName ?? string.Empty;
            return fullName.Length > 0;
        } catch (Exception ex) when (ex is FileNotFoundException or TypeLoadException) {
            return false;
        }
    }
}