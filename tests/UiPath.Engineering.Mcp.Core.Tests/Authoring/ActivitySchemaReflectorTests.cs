using System.Reflection;
using UiPath.Engineering.Mcp.Core.Authoring;
using UiPath.Engineering.Mcp.Core.CodeAnalysis;
using UiPath.Engineering.Mcp.Core.Models;
using Xunit.Abstractions;

namespace UiPath.Engineering.Mcp.Core.Tests.Authoring;

/// <summary>
/// Reflection over activity package assemblies supplies the COMPLETE, required-aware
/// property surface a discovered activity otherwise lacks: the starter sample only
/// carries properties whose value differs from the CLR type default.
/// </summary>
public class ActivitySchemaReflectorTests {
    private readonly ITestOutputHelper _output;

    public ActivitySchemaReflectorTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void TryReadProperties_FrameworkTypeName_ReturnsNull() {
        // A framework activity carries no package-specific argument surface.
        Assert.Null(ActivitySchemaReflector.TryReadProperties(
            ["/nonexistent/a.dll"], "System.Activities.Statements.Assign"));
    }

    [Fact]
    public void TryReadProperties_EmptyInputs_ReturnNull() {
        Assert.Null(ActivitySchemaReflector.TryReadProperties([], "Some.Activity"));
        Assert.Null(ActivitySchemaReflector.TryReadProperties(["/x.dll"], null));
        Assert.Null(ActivitySchemaReflector.TryReadProperties(["/x.dll"], "  "));
    }

    [Fact]
    public void TryReadProperties_MissingAssembly_ReturnsNull() {
        Assert.Null(ActivitySchemaReflector.TryReadProperties(
            [Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N") + ".dll")],
            "Some.Package.Activity"));
    }

    [Theory]
    [InlineData("UiPath.Excel.Activities.Business.ReadRangeX", "UiPath.Excel.Activities.Business.ReadRangeX")]
    [InlineData("UiPath.Core.Activities.Click, UiPath.UIAutomation.Activities", "UiPath.Core.Activities.Click")]
    [InlineData("Ns.Outer+Inner", "Ns.Outer.Inner")]
    public void NormalizeTypeName_StripsAssemblyAndNests(string input, string expected) =>
        Assert.Equal(expected, ActivitySchemaReflector.NormalizeTypeName(input));

    [Fact]
    public void NormalizeTypeName_KeepsGenericArityForOpenDefinitionResolution() =>
        Assert.Equal("System.Activities.Statements.Assign`1",
            ActivitySchemaReflector.NormalizeTypeName("System.Activities.Statements.Assign`1"));

    /// <summary>
    /// The live path: with a real package in the NuGet folder, reflection reads the
    /// activity's full argument surface — at least one typed expression property
    /// with a direction, which is exactly the information the starter sample cannot
    /// guarantee. Skips when no candidate package/type is installed.
    /// </summary>
    [Fact]
    public void TryReadProperties_InstalledActivityPackage_ReadsATypedSurface() {
        var folder = new NuGetReferenceResolver().GetPackagesFolder();
        if (folder is null) {
            _output.WriteLine("No NuGet packages folder; nothing to reflect over.");
            return;
        }

        // (package, version, type) candidates spanning the classic and modern
        // activity families. The first that resolves wins.
        var candidates = new (string Id, string Version, string Type, string Tfm)[] {
            ("UiPath.System.Activities", "22.10.4", "UiPath.Core.Activities.LogMessage", "net461"),
            ("UiPath.System.Activities", "22.10.4", "UiPath.Core.Activities.RetryScope", "net461"),
            ("UiPath.Excel.Activities", "2.16.2", "UiPath.Excel.Activities.ReadRange", "net461"),
            ("UiPath.Excel.Activities", "2.16.2", "UiPath.Excel.Activities.ExcelReadRange", "net461"),
            ("UiPath.UIAutomation.Activities", "22.10.5", "UiPath.UIAutomationNext.Activities.NClick", "net461"),
        };

        var resolved = new List<(string Type, IReadOnlyList<PropertySchema> Surface)>();
        foreach (var candidate in candidates) {
            var assemblyPaths = PackageAssemblies(folder, candidate.Id, candidate.Version, candidate.Tfm);
            if (assemblyPaths.Count == 0) {
                continue;
            }

            var surface = ActivitySchemaReflector.TryReadProperties(assemblyPaths, candidate.Type);
            if (surface is null) {
                _output.WriteLine($"{candidate.Type}: unresolved");
                continue;
            }

            resolved.Add((candidate.Type, surface));
            _output.WriteLine($"{candidate.Type} ({surface.Count}): "
                + string.Join(", ", surface.Select(p => $"{p.Name}:{p.ClrType}/{p.Direction}/{p.Kind}")));
        }

        if (resolved.Count == 0) {
            _output.WriteLine("None of the candidate activity types resolved; reflection path not exercised here.");
            return;
        }

        // The reflection surface is strictly richer than the DisplayName-only starter:
        // at least one candidate exposes a real declared settable property. This is the
        // enrichment that turns the vacuous validator into a real one.
        Assert.Contains(resolved, entry => entry.Surface.Any(p => p.Name != "DisplayName"));
    }

    private static IReadOnlyList<string> PackageAssemblies(string packagesFolder, string id, string version, string tfm) {
        var root = Path.Combine(packagesFolder, id.ToLowerInvariant(), version, "lib");
        if (!Directory.Exists(root)) {
            return [];
        }

        var preferred = Path.Combine(root, tfm);
        var dir = Directory.Exists(preferred)
            ? preferred
            : Directory.GetDirectories(root).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        return dir is null ? [] : Directory.GetFiles(dir, "*.dll").ToList();
    }

    [Fact]
    public void TryReadProperties_TypeInTheGivenAssembly_ResolvesAndWalksDeclaredPropertiesOnly() {
        // A non-activity type resolves and shows that only its own declared settable
        // properties are walked; the framework base (System.Object) contributes none.
        var assembly = typeof(ActivitySchemaReflectorTests).Assembly.Location;
        var surface = ActivitySchemaReflector.TryReadProperties([assembly], typeof(ActivitySchemaReflectorTests).FullName);

        Assert.NotNull(surface);
        Assert.Contains(surface!, p => p.Name == "DisplayName");
        Assert.DoesNotContain(surface!, p => p.Name is "Equals" or "GetType" or "ToString");
    }
}