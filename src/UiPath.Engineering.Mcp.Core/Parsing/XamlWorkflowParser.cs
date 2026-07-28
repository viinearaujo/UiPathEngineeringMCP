using System.Xml.Linq;
using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Core.Parsing;

/// <summary>
/// Parses a UiPath .xaml workflow file into a <see cref="WorkflowModel"/>.
/// Matching is done on <see cref="XName.LocalName"/> only, ignoring UiPath's heavy namespacing.
/// Malformed XAML never throws; the returned model carries <see cref="WorkflowModel.HasParseError"/>.
/// </summary>
public sealed class XamlWorkflowParser
{
    // Elements that are XAML infrastructure / attached-property containers, not activities.
    private static readonly HashSet<string> NonActivityElements = new(StringComparer.Ordinal)
    {
        "Activity", "ActivityBuilder", "Members", "Property", "Variable", "Reference",
        "Collection", "Dictionary", "Array", "Key", "AssemblyReference",
        "InArgument", "OutArgument", "InOutArgument", "Literal",
        "DelegateInArgument", "DelegateOutArgument", "DelegateInReference", "DelegateOutReference",
        "ActivityAction", "Catch", "String", "Boolean", "Int32", "Int64", "Double",
        "Object", "Null", "VisualBasicValue", "VisualBasicReference", "CSharpValue", "CSharpReference"
    };

    public WorkflowModel Parse(string fileName, string filePath, string xamlContent)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(xamlContent);
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or InvalidOperationException)
        {
            return new WorkflowModel
            {
                FileName = fileName,
                FilePath = filePath,
                HasParseError = true,
                ParseError = $"XAML parse failure: {ex.Message}"
            };
        }

        var model = new WorkflowModel { FileName = fileName, FilePath = filePath };
        if (doc.Root is null)
        {
            model.HasParseError = true;
            model.ParseError = "XAML parse failure: document has no root element.";
            return model;
        }

        ExtractArguments(doc, model);
        ExtractVariables(doc, model);
        Walk(doc.Root, 0, model);
        return model;
    }

    private static void ExtractArguments(XDocument doc, WorkflowModel model)
    {
        foreach (var prop in doc.Descendants().Where(e => e.Name.LocalName == "Property"))
        {
            var typeAttr = prop.Attribute("Type")?.Value;
            var name = prop.Attribute("Name")?.Value;
            if (typeAttr is null || name is null)
            {
                continue;
            }

            var direction = typeAttr switch
            {
                _ when typeAttr.StartsWith("InOutArgument", StringComparison.Ordinal) => "In/Out",
                _ when typeAttr.StartsWith("InArgument", StringComparison.Ordinal) => "In",
                _ when typeAttr.StartsWith("OutArgument", StringComparison.Ordinal) => "Out",
                _ => null
            };
            if (direction is null)
            {
                continue;
            }

            model.Arguments.Add(new ArgumentModel
            {
                Name = name,
                Direction = direction,
                Type = ExtractInnerType(typeAttr)
            });
        }
    }

    private static void ExtractVariables(XDocument doc, WorkflowModel model)
    {
        foreach (var variable in doc.Descendants().Where(e => e.Name.LocalName == "Variable"))
        {
            var name = variable.Attribute("Name")?.Value;
            if (name is null)
            {
                continue;
            }

            model.Variables.Add(new VariableModel
            {
                Name = name,
                Type = ExtractTypeArguments(variable),
                DefaultValue = variable.Attribute("Default")?.Value,
                Scope = variable.Ancestors()
                    .Select(a => a.Attribute("DisplayName")?.Value)
                    .FirstOrDefault(d => d is not null)
            });
        }
    }

    private void Walk(XElement element, int depth, WorkflowModel model)
    {
        foreach (var child in element.Elements())
        {
            var local = child.Name.LocalName;
            if (local.Contains('.') || NonActivityElements.Contains(local))
            {
                // Attached-property containers (Sequence.Variables, TryCatch.Catch, ViewState, ...)
                // and XAML primitives: transparent, recurse at the same depth.
                Walk(child, depth, model);
                continue;
            }

            if (local == "TryCatch")
            {
                ExtractTryCatch(child, model);
            }
            else if (local == "InvokeWorkflowFile")
            {
                model.InvokeWorkflows.Add(new InvokeWorkflowModel
                {
                    SourceWorkflow = model.FileName,
                    TargetWorkflow = child.Attribute("WorkflowFileName")?.Value
                        ?? child.Attribute("FileName")?.Value
                        ?? string.Empty,
                    DisplayName = child.Attribute("DisplayName")?.Value ?? string.Empty
                });
            }
            else if (local == "LogMessage")
            {
                model.LogMessages.Add(new LogMessageModel
                {
                    DisplayName = child.Attribute("DisplayName")?.Value ?? string.Empty,
                    Level = child.Attribute("Level")?.Value ?? string.Empty,
                    Message = child.Attribute("Message")?.Value
                        ?? child.Attribute("MessageText")?.Value
                        ?? string.Empty
                });
            }

            model.Activities.Add(new ActivityModel
            {
                DisplayName = child.Attribute("DisplayName")?.Value ?? local,
                Type = local,
                Depth = depth
            });
            Walk(child, depth + 1, model);
        }
    }

    private static void ExtractTryCatch(XElement tryCatch, WorkflowModel model)
    {
        var catchTypes = tryCatch.Descendants()
            .Where(e => e.Name.LocalName == "Catch")
            .Select(ExtractTypeArguments)
            .Where(t => t.Length > 0)
            .ToList();

        model.ExceptionHandlers.Add(new ExceptionHandlerModel
        {
            WorkflowName = model.FileName,
            HasGlobalHandler = true,
            CatchTypes = catchTypes
        });
    }

    private static string ExtractTypeArguments(XElement element) =>
        element.Attributes().FirstOrDefault(a => a.Name.LocalName == "TypeArguments")?.Value ?? string.Empty;

    private static string ExtractInnerType(string argumentType)
    {
        var start = argumentType.IndexOf('(');
        var end = argumentType.LastIndexOf(')');
        return start >= 0 && end > start
            ? argumentType.Substring(start + 1, end - start - 1).Trim()
            : string.Empty;
    }
}
