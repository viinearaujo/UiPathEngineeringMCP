namespace UiPath.Engineering.Mcp.Core.Templates;

// Templates for blank UiPath XAML workflows. x:Class must match the file's path
// relative to the project root (minus .xaml) with folder separators replaced by
// underscores — see UiPath's XAML naming rules.
public static class XamlWorkflowTemplates {
    public static string BlankWorkflow(string xamlClassName) => $$"""
        <Activity mc:Ignorable="sap sap2010" x:Class="{{xamlClassName}}"
          xmlns="http://schemas.microsoft.com/netfx/2009/xaml/activities"
          xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
          xmlns:sap="http://schemas.microsoft.com/netfx/2009/xaml/activities/presentation"
          xmlns:sap2010="http://schemas.microsoft.com/netfx/2010/xaml/activities/presentation"
          xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <x:Members>
          </x:Members>
          <Sequence DisplayName="{{xamlClassName}}" />
        </Activity>
        """;

    // "Workflows/SendEmail" -> "Workflows_SendEmail"; strips a trailing .xaml if present.
    public static string ToXamlClassName(string relativePathWithoutExtension) {
        var path = relativePathWithoutExtension.Replace('\\', '/').Trim('/');
        if (path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)) {
            path = path[..^".xaml".Length];
        }
        return path.Replace('/', '_');
    }
}
