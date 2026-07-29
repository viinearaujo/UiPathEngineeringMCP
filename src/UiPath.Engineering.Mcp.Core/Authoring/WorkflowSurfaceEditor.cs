using System.Xml;
using System.Xml.Linq;

namespace UiPath.Engineering.Mcp.Core.Authoring;

/// <summary>
/// Surface-level edits on a UiPath .xaml workflow: add, remove, or rename arguments
/// (<x:Property> children of the root Activity) and variables (in the root Sequence's
/// <Sequence.Variables> block, created when absent). Whitespace is preserved so
/// untouched regions stay byte-identical; edits never throw, failures come back as
/// <see cref="SurfaceEditResult.Error"/> with a typed <see cref="SurfaceEditResult.ErrorCode"/>.
/// Rename updates the declaration only and reports a warning that expressions
/// referencing the old name are not rewritten.
/// </summary>
public static class WorkflowSurfaceEditor {
    public const string Add = "add";
    public const string Remove = "remove";
    public const string Rename = "rename";

    public const string Variable = "variable";
    public const string Argument = "argument";

    private static readonly XNamespace Wf = "http://schemas.microsoft.com/netfx/2009/xaml/activities";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    public static SurfaceEditResult Edit(
        string xamlContent,
        string operation,
        string kind,
        string name,
        string? type = null,
        string? direction = null,
        string? newName = null,
        string? defaultValue = null) {
        var normalizedOperation = operation?.Trim().ToLowerInvariant();
        if (normalizedOperation is not (Add or Remove or Rename)) {
            return SurfaceEditResult.Failure($"Unknown operation '{operation}'. Use add, remove, or rename.");
        }

        var normalizedKind = kind?.Trim().ToLowerInvariant();
        if (normalizedKind is not (Variable or Argument)) {
            return SurfaceEditResult.Failure($"Unknown kind '{kind}'. Use variable or argument.");
        }

        if (string.IsNullOrWhiteSpace(name)) {
            return SurfaceEditResult.Failure("name is required.");
        }

        XDocument doc;
        try {
            doc = XDocument.Parse(xamlContent, LoadOptions.PreserveWhitespace);
        } catch (Exception ex) when (ex is XmlException or InvalidOperationException) {
            return SurfaceEditResult.Failure($"XAML parse failure: {ex.Message}");
        }

        return normalizedKind == Variable
            ? EditVariable(doc, normalizedOperation!, name, type, newName, defaultValue)
            : EditArgument(doc, normalizedOperation!, name, type, direction, newName);
    }

    private static SurfaceEditResult EditVariable(
        XDocument doc, string operation, string name, string? type, string? newName, string? defaultValue) {
        var sequence = RootSequence(doc);
        if (sequence is null) {
            return SurfaceEditResult.Failure("No root <Sequence> found in the workflow.");
        }
        var block = sequence.Elements(Wf + "Sequence.Variables").FirstOrDefault();
        var existing = block?.Elements(Wf + "Variable")
            .FirstOrDefault(v => string.Equals(v.Attribute("Name")?.Value, name, StringComparison.Ordinal));

        switch (operation) {
            case Add:
                if (string.IsNullOrWhiteSpace(type)) {
                    return SurfaceEditResult.Failure("type is required when adding a variable.");
                }
                if (existing is not null) {
                    return SurfaceEditResult.Failure(
                        $"A variable named '{name}' already exists in the root Sequence.",
                        ToolErrorCodes.DataDeclarationConflict);
                }
                var variable = new XElement(Wf + "Variable",
                    new XAttribute(X + "TypeArguments", TypeToken.Render(type)),
                    new XAttribute("Name", name));
                if (defaultValue is not null) {
                    variable.Add(new XAttribute("Default", defaultValue));
                }
                AddToVariablesBlock(sequence, block, variable);
                break;

            case Remove:
                if (existing is null) {
                    return SurfaceEditResult.Failure(
                        $"No variable named '{name}' found in the root Sequence.",
                        ToolErrorCodes.DataDeclarationNotFound);
                }
                RemoveElement(existing);
                break;

            case Rename:
                if (string.IsNullOrWhiteSpace(newName)) {
                    return SurfaceEditResult.Failure("newName is required for rename.");
                }
                if (existing is null) {
                    return SurfaceEditResult.Failure(
                        $"No variable named '{name}' found in the root Sequence.",
                        ToolErrorCodes.DataDeclarationNotFound);
                }
                existing.SetAttributeValue("Name", newName);
                return SurfaceEditResult.Ok(Serialize(doc), [RenameWarning("variable", name, newName)]);
        }

        return SurfaceEditResult.Ok(Serialize(doc), []);
    }

    private static SurfaceEditResult EditArgument(
        XDocument doc, string operation, string name, string? type, string? direction, string? newName) {
        var root = doc.Root;
        if (root is null || root.Name.LocalName != "Activity") {
            return SurfaceEditResult.Failure("No root <Activity> element found in the workflow.");
        }
        var existing = root.Elements(X + "Property")
            .FirstOrDefault(p => string.Equals(p.Attribute("Name")?.Value, name, StringComparison.Ordinal));

        switch (operation) {
            case Add:
                if (string.IsNullOrWhiteSpace(type)) {
                    return SurfaceEditResult.Failure("type is required when adding an argument.");
                }
                var wrapper = ArgumentWrapper(direction);
                if (wrapper is null) {
                    return SurfaceEditResult.Failure($"Unknown direction '{direction}'. Use In, Out, or In/Out.");
                }
                if (existing is not null) {
                    return SurfaceEditResult.Failure(
                        $"An argument named '{name}' already exists.",
                        ToolErrorCodes.DataDeclarationConflict);
                }
                var property = new XElement(X + "Property",
                    new XAttribute("Name", name),
                    new XAttribute("Type", $"{wrapper}({TypeToken.Render(type)})"));
                AddArgumentProperty(root, property);
                break;

            case Remove:
                if (existing is null) {
                    return SurfaceEditResult.Failure(
                        $"No argument named '{name}' found.",
                        ToolErrorCodes.DataDeclarationNotFound);
                }
                RemoveElement(existing);
                break;

            case Rename:
                if (string.IsNullOrWhiteSpace(newName)) {
                    return SurfaceEditResult.Failure("newName is required for rename.");
                }
                if (existing is null) {
                    return SurfaceEditResult.Failure(
                        $"No argument named '{name}' found.",
                        ToolErrorCodes.DataDeclarationNotFound);
                }
                existing.SetAttributeValue("Name", newName);
                return SurfaceEditResult.Ok(Serialize(doc), [RenameWarning("argument", name, newName)]);
        }

        return SurfaceEditResult.Ok(Serialize(doc), []);
    }

    private static XElement? RootSequence(XDocument doc) =>
        doc.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Sequence");

    private static string? ArgumentWrapper(string? direction) =>
        (direction ?? "In").Trim().ToLowerInvariant() switch {
            "in" => "InArgument",
            "out" => "OutArgument",
            "in/out" => "InOutArgument",
            _ => null
        };

    private static void AddToVariablesBlock(XElement sequence, XElement? block, XElement variable) {
        if (block is null) {
            // Create the block as the first child of the Sequence, mirroring its indentation.
            var ownIndent = GetIndent(sequence);
            block = new XElement(Wf + "Sequence.Variables");
            if (sequence.FirstNode is not null) {
                sequence.FirstNode.AddBeforeSelf(new XText("\n" + ownIndent + "  "), block);
            } else {
                sequence.Add(new XText("\n" + ownIndent + "  "), block, new XText("\n" + ownIndent));
            }
        }

        var blockIndent = GetIndent(block);
        if (block.LastNode is XText trailing && string.IsNullOrWhiteSpace(trailing.Value)) {
            trailing.Remove();
        }
        block.Add(new XText("\n" + blockIndent + "  "), variable, new XText("\n" + blockIndent));
    }

    private static void AddArgumentProperty(XElement root, XElement property) {
        // x:Property declarations sit before the workflow body, right after any existing ones.
        var lastProperty = root.Elements(X + "Property").LastOrDefault();
        var ownIndent = GetIndent(lastProperty ?? root);
        if (lastProperty is not null) {
            lastProperty.AddAfterSelf(new XText("\n" + ownIndent), property);
            return;
        }
        if (root.FirstNode is not null) {
            root.FirstNode.AddBeforeSelf(new XText("\n" + ownIndent + "  "), property);
        } else {
            root.Add(new XText("\n" + ownIndent + "  "), property, new XText("\n" + ownIndent));
        }
    }

    private static void RemoveElement(XElement element) {
        if (element.PreviousNode is XText leading && string.IsNullOrWhiteSpace(leading.Value)) {
            leading.Remove();
        }
        element.Remove();
    }

    private static string GetIndent(XElement element) {
        if (element.PreviousNode is XText text) {
            var value = text.Value;
            var lastNewline = value.LastIndexOf('\n');
            if (lastNewline >= 0) {
                return value[(lastNewline + 1)..];
            }
        }
        return string.Empty;
    }

    private static string RenameWarning(string kind, string oldName, string newName) =>
        $"Rename updated the {kind} declaration only ('{oldName}' → '{newName}'); " +
        $"expressions referencing '{oldName}' are not rewritten — update them yourself.";

    private static string Serialize(XDocument doc) {
        var settings = new XmlWriterSettings {
            Indent = false,
            OmitXmlDeclaration = doc.Declaration is null,
            Encoding = System.Text.Encoding.UTF8
        };
        using var writer = new StringWriterWithEncoding(System.Text.Encoding.UTF8);
        using (var xml = XmlWriter.Create(writer, settings)) {
            doc.Save(xml);
        }
        return writer.ToString();
    }

    // XmlWriter picks the encoding from the TextWriter; StringWriter reports UTF-16,
    // which would rewrite the declaration to utf-16 while callers write UTF-8 files.
    private sealed class StringWriterWithEncoding : StringWriter {
        private readonly System.Text.Encoding _encoding;
        public StringWriterWithEncoding(System.Text.Encoding encoding) => _encoding = encoding;
        public override System.Text.Encoding Encoding => _encoding;
    }
}

public sealed record SurfaceEditResult(
    bool Success, string? Error, string? UpdatedContent, List<string> Warnings, string? ErrorCode = null) {
    public static SurfaceEditResult Ok(string content, List<string> warnings) => new(true, null, content, warnings);
    public static SurfaceEditResult Failure(string error, string? errorCode = null) => new(false, error, null, [], errorCode);
}
