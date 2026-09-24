using System.Xml.Linq;

namespace UiPath.Engineering.Mcp.Core.Authoring;

/// <summary>
/// The namespace URIs the designer-state attributes and ViewState dictionaries
/// use. Kept in one place so XamlBuilder and XamlViewStateEmitter cannot drift.
/// </summary>
internal static class ViewStateNamespaces {
    internal const string Av = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    internal static readonly XNamespace AvPoint = Av;
    internal static readonly XNamespace AvSize = Av;
    internal static readonly XNamespace AvPointCollection = Av;

    internal static readonly XName PointName = AvPoint + "Point";
    internal static readonly XName SizeName = AvSize + "Size";
    internal static readonly XName PointCollectionName = AvPointCollection + "PointCollection";
}