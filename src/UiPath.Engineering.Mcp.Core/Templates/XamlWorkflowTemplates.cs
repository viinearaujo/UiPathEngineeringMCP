using UiPath.Engineering.Mcp.Core.Authoring;

namespace UiPath.Engineering.Mcp.Core.Templates;

// Templates for blank UiPath XAML workflows. x:Class must match the file's path
// relative to the project root (minus .xaml) with folder separators replaced by
// underscores — see UiPath's XAML naming rules.
public static class XamlWorkflowTemplates {
    /// <summary>
    /// A blank workflow file. <paramref name="displayName"/> sets the root
    /// Sequence's DisplayName — what Studio shows on the canvas. It defaults to
    /// the file name (not the <c>x:Class</c> name, which would surface
    /// "Workflows_SendEmail" on the canvas).
    /// </summary>
    public static string BlankWorkflow(string xamlClassName, string? displayName = null) {
        var sequenceName = string.IsNullOrWhiteSpace(displayName)
            ? DefaultDisplayName(xamlClassName)
            : displayName;
        var core = ProjectXamlSettings.ModernCoreAssembly;

        return $$"""
        <Activity mc:Ignorable="sap sap2010" x:Class="{{xamlClassName}}"
          xmlns="{{XamlNamespaces.WfUri}}"
          xmlns:mc="{{XamlNamespaces.McUri}}"
          xmlns:sap="{{XamlNamespaces.SapUri}}"
          xmlns:sap2010="{{XamlNamespaces.Sap2010Uri}}"
          xmlns:sco="{{XamlNamespaces.ClrObjectModel(core)}}"
          xmlns:scg="{{XamlNamespaces.ClrGeneric(core)}}"
          xmlns:x="{{XamlNamespaces.XUri}}">
          <x:Members>
          </x:Members>
          <TextExpression.NamespacesForImplementation>
            <sco:Collection x:TypeArguments="x:String">
              <x:String>System</x:String>
              <x:String>System.Collections.Generic</x:String>
              <x:String>System.Linq</x:String>
              <x:String>System.Threading.Tasks</x:String>
              <x:String>UiPath.Core</x:String>
              <x:String>UiPath.Core.Activities</x:String>
            </sco:Collection>
          </TextExpression.NamespacesForImplementation>
          <Sequence DisplayName="{{sequenceName}}" />
        </Activity>
        """;
    }

    // "Workflows_SendEmail" -> "SendEmail"; "SendEmail" -> "SendEmail".
    internal static string DefaultDisplayName(string xamlClassName) {
        var name = xamlClassName;
        if (name.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)) {
            name = name[..^".xaml".Length];
        }

        var lastSeparator = name.LastIndexOf('_');
        return lastSeparator >= 0 && lastSeparator < name.Length - 1
            ? name[(lastSeparator + 1)..]
            : name;
    }

    // "Workflows/SendEmail" -> "Workflows_SendEmail"; strips a trailing .xaml if present.
    public static string ToXamlClassName(string relativePathWithoutExtension) {
        var path = relativePathWithoutExtension.Replace('\\', '/').Trim('/');
        if (path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)) {
            path = path[..^".xaml".Length];
        }
        return path.Replace('/', '_');
    }
}
