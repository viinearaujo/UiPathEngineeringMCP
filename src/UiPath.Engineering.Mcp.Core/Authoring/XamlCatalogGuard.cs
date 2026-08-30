using System.Xml;
using System.Xml.Linq;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Core.Parsing;

namespace UiPath.Engineering.Mcp.Core.Authoring;

public static class XamlCatalogGuard
{
    public static List<ToolError> FindUnknownActivities(string xaml, IActivityCatalog catalog)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(xaml);
        }
        catch (Exception ex) when (ex is XmlException or InvalidOperationException)
        {
            return
            [
                new ToolError(
                    ToolErrorCodes.XamlParseFailed,
                    $"The XAML could not be parsed, so activity types cannot be checked against the catalog: {ex.Message}",
                    "Fix the XAML, use build_workflow from a spec, or pass allowUnknownActivities: true only as an escape hatch.")
            ];
        }

        var unknown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var located in XamlActivityLocator.Locate(doc))
        {
            var name = located.Element.Name.LocalName;
            if (!catalog.TryGet(name, out _))
            {
                unknown.Add(name);
            }
        }

        return unknown
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Select(name =>
            {
                var suggestion = catalog.Suggest(name);
                var hint = suggestion is null
                    ? "Call recommend_activities for a catalog name, then author with validate_activity_spec / build_workflow."
                    : $"Did you mean \"{suggestion}\"? Call recommend_activities, then author with validate_activity_spec / build_workflow.";
                return new ToolError(
                    ToolErrorCodes.SpecUnknownActivity,
                    $"XAML contains activity \"{name}\" which is not in the project activity catalog.",
                    hint,
                    "recommend_activities");
            })
            .ToList();
    }
}
