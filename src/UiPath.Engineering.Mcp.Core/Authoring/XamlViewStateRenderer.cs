using System.Xml.Linq;

namespace UiPath.Engineering.Mcp.Core.Authoring;

/// <summary>
/// Declares ViewState-related xmlns prefixes on a rendered workflow root.
/// </summary>
public static class XamlViewStateRenderer {
    // ViewState binds scg: and x:, so the root must declare scg:. sap/sap2010 are
    // already declared on the <Activity> root; scg: is not.
    public static void DeclareRootNamespace(XElement root, string? coreAssembly) {
        if (root.Attribute(XNamespace.Xmlns + "scg") is null) {
            var core = coreAssembly ?? ProjectXamlSettings.ModernCoreAssembly;
            root.Add(new XAttribute(XNamespace.Xmlns + "scg", XamlNamespaces.ClrGeneric(core)));
        }
    }
}
