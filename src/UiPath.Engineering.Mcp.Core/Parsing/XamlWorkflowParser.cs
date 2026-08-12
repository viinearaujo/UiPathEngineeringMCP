using System.Xml.Linq;
using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Core.Parsing;

/// <summary>
/// Parses a UiPath .xaml workflow file into a <see cref="WorkflowModel"/>.
/// Matching is done on <see cref="XName.LocalName"/> only, ignoring UiPath's heavy namespacing.
/// Malformed XAML never throws; the returned model carries <see cref="WorkflowModel.HasParseError"/>.
/// </summary>
public sealed class XamlWorkflowParser {
    // Elements that are XAML infrastructure / attached-property containers, not activities.
    // Internal: shared with XamlActivityEditor so both classify elements identically.
    internal static readonly HashSet<string> NonActivityElements = new(StringComparer.Ordinal)
    {
        "Activity", "ActivityBuilder", "Members", "Property", "Variable", "Reference",
        "Collection", "Dictionary", "Array", "Key", "AssemblyReference",
        "InArgument", "OutArgument", "InOutArgument", "Literal",
        "DelegateInArgument", "DelegateOutArgument", "DelegateInReference", "DelegateOutReference",
        "ActivityAction", "Catch", "String", "Boolean", "Int32", "Int64", "Double",
        "Object", "Null", "VisualBasicValue", "VisualBasicReference", "CSharpValue", "CSharpReference"
    };

    public WorkflowModel Parse(string fileName, string filePath, string xamlContent) {
        XDocument doc;
        try {
            doc = XDocument.Parse(xamlContent, LoadOptions.SetLineInfo);
        } catch (Exception ex) when (ex is System.Xml.XmlException or InvalidOperationException) {
            return new WorkflowModel {
                FileName = fileName,
                FilePath = filePath,
                HasParseError = true,
                ParseError = $"XAML parse failure: {ex.Message}"
            };
        }

        var model = new WorkflowModel { FileName = fileName, FilePath = filePath };
        if (doc.Root is null) {
            model.HasParseError = true;
            model.ParseError = "XAML parse failure: document has no root element.";
            return model;
        }

        // Studio surfaces a workflow-level annotation as the description of the workflow.
        model.Description = doc.Root.Attributes()
            .FirstOrDefault(a => a.Name.LocalName == "Annotation.AnnotationText")?.Value;

        ExtractArguments(doc, model);
        ExtractVariables(doc, model);
        PopulateActivities(doc, model);
        return model;
    }

    private static void ExtractArguments(XDocument doc, WorkflowModel model) {
        foreach (var prop in doc.Descendants().Where(e => e.Name.LocalName == "Property")) {
            var typeAttr = prop.Attribute("Type")?.Value;
            var name = prop.Attribute("Name")?.Value;
            if (typeAttr is null || name is null) {
                continue;
            }

            var direction = typeAttr switch {
                _ when typeAttr.StartsWith("InOutArgument", StringComparison.Ordinal) => "In/Out",
                _ when typeAttr.StartsWith("InArgument", StringComparison.Ordinal) => "In",
                _ when typeAttr.StartsWith("OutArgument", StringComparison.Ordinal) => "Out",
                _ => null
            };
            if (direction is null) {
                continue;
            }

            model.Arguments.Add(new ArgumentModel {
                Name = name,
                Direction = direction,
                Type = ExtractInnerType(typeAttr)
            });
        }
    }

    private static void ExtractVariables(XDocument doc, WorkflowModel model) {
        foreach (var variable in doc.Descendants().Where(e => e.Name.LocalName == "Variable")) {
            var name = variable.Attribute("Name")?.Value;
            if (name is null) {
                continue;
            }

            model.Variables.Add(new VariableModel {
                Name = name,
                Type = ExtractTypeArguments(variable),
                DefaultValue = variable.Attribute("Default")?.Value,
                Scope = variable.Ancestors()
                    .Select(a => a.Attribute("DisplayName")?.Value)
                    .FirstOrDefault(d => d is not null)
            });
        }
    }

    private void PopulateActivities(XDocument doc, WorkflowModel model) {
        var byId = new Dictionary<string, ActivityModel>(StringComparer.Ordinal);
        foreach (var located in XamlActivityLocator.Locate(doc)) {
            var element = located.Element;
            var local = element.Name.LocalName;

            if (local == "TryCatch") {
                ExtractTryCatch(element, model);
            } else if (local == "InvokeWorkflowFile") {
                model.InvokeWorkflows.Add(ExtractInvokeWorkflow(element, model.FileName));
            } else if (local == "LogMessage") {
                model.LogMessages.Add(new LogMessageModel {
                    DisplayName = element.Attribute("DisplayName")?.Value ?? string.Empty,
                    Level = element.Attribute("Level")?.Value ?? string.Empty,
                    Message = element.Attribute("Message")?.Value
                        ?? element.Attribute("MessageText")?.Value
                        ?? string.Empty
                });
            }

            var activity = new ActivityModel {
                Id = located.Id,
                ParentId = located.ParentId,
                DisplayName = element.Attribute("DisplayName")?.Value ?? local,
                Type = local,
                Depth = located.Depth,
                Order = located.Order,
                Line = located.Line
            };
            model.Activities.Add(activity);
            byId[located.Id] = activity;
            // Pre-order traversal guarantees the parent was already added.
            if (located.ParentId is not null && byId.TryGetValue(located.ParentId, out var parent)) {
                parent.Children.Add(activity);
            }
        }
    }

    private static InvokeWorkflowModel ExtractInvokeWorkflow(XElement element, string sourceFileName) {
        var invoke = new InvokeWorkflowModel {
            SourceWorkflow = sourceFileName,
            TargetWorkflow = element.Attribute("WorkflowFileName")?.Value
                ?? element.Attribute("FileName")?.Value
                ?? string.Empty,
            DisplayName = element.Attribute("DisplayName")?.Value ?? string.Empty
        };

        var container = element.Elements()
            .FirstOrDefault(e => e.Name.LocalName == "InvokeWorkflowFile.Arguments");
        if (container is null) {
            return invoke;
        }

        foreach (var argument in container.Elements()) {
            var direction = argument.Name.LocalName switch {
                "InArgument" => "In",
                "OutArgument" => "Out",
                "InOutArgument" => "In/Out",
                _ => null
            };
            var key = argument.Attributes().FirstOrDefault(a => a.Name.LocalName == "Key")?.Value;
            if (direction is null || key is null) {
                continue;
            }

            invoke.ArgumentMappings.Add(new ArgumentMappingModel {
                Direction = direction,
                TargetArgument = key,
                Expression = ExtractExpressionText(argument)
            });
        }
        return invoke;
    }

    // Simple bindings are literal text ([expr]); VisualBasicReference-style bindings
    // carry the expression on an ExpressionText attribute instead.
    private static string ExtractExpressionText(XElement argument) {
        var expressionText = argument.Descendants()
            .Select(d => d.Attribute("ExpressionText")?.Value)
            .FirstOrDefault(t => t is not null);
        return expressionText ?? argument.Value.Trim();
    }

    private static void ExtractTryCatch(XElement tryCatch, WorkflowModel model) {
        var catchTypes = tryCatch.Descendants()
            .Where(e => e.Name.LocalName == "Catch")
            .Select(ExtractTypeArguments)
            .Where(t => t.Length > 0)
            .ToList();

        model.ExceptionHandlers.Add(new ExceptionHandlerModel {
            WorkflowName = model.FileName,
            HasGlobalHandler = true,
            CatchTypes = catchTypes
        });
    }

    private static string ExtractTypeArguments(XElement element) =>
        element.Attributes().FirstOrDefault(a => a.Name.LocalName == "TypeArguments")?.Value ?? string.Empty;

    private static string ExtractInnerType(string argumentType) {
        var start = argumentType.IndexOf('(');
        var end = argumentType.LastIndexOf(')');
        return start >= 0 && end > start
            ? argumentType[(start + 1)..end].Trim()
            : string.Empty;
    }
}
