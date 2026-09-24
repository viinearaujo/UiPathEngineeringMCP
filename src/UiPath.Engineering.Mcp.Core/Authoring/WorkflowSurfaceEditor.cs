using System.Xml;
using System.Xml.Linq;

namespace UiPath.Engineering.Mcp.Core.Authoring;

/// <summary>
/// Surface-level edits on a UiPath .xaml workflow: add, remove, or rename arguments
/// (<x:Property> children of root <x:Members>) and variables (in the root Sequence's
/// <Sequence.Variables> block, created when absent). Whitespace is preserved so
/// untouched regions stay byte-identical; edits never throw, failures come back as
/// <see cref="SurfaceEditResult.Error"/> with a typed <see cref="SurfaceEditResult.ErrorCode"/>.
/// Rename rewrites the declaration and every expression that references the old
/// name; it reports how many expressions were updated.
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

    // TypeToken emits s:/sd: tokens for BCL/System.Data types; a document that never
    // declared those aliases would not resolve them. After any edit, declare every
    // alias the document's type tokens actually use.
    private static void EnsureTypeAliases(XDocument doc) {
        if (doc.Root is null) {
            return;
        }

        var aliases = new HashSet<string>(StringComparer.Ordinal);
        foreach (var attribute in doc.Root.DescendantsAndSelf().SelectMany(e => e.Attributes())) {
            if (attribute.IsNamespaceDeclaration) {
                continue;
            }

            var value = attribute.Name.LocalName switch {
                "TypeArguments" => attribute.Value,
                "Type" => UnwrapArgumentWrapper(attribute.Value),
                _ => null
            };
            if (value is null) {
                continue;
            }

            foreach (var alias in TypeToken.AliasesIn(value)) {
                aliases.Add(alias);
            }
        }

        foreach (var alias in aliases) {
            RegisterAlias(doc.Root, alias);
        }
    }

    // "InArgument(x:String)" -> "x:String"; "x:String, Argument" is unchanged.
    private static string UnwrapArgumentWrapper(string value) {
        var open = value.IndexOf('(');
        var close = value.LastIndexOf(')');
        return open >= 0 && close > open
            ? value[(open + 1)..close].Trim()
            : value;
    }

    private static void RegisterAlias(XElement root, string alias) {
        var declaration = XNamespace.Xmlns + alias;
        if (root.Attribute(declaration) is not null) {
            return;
        }

        var core = ExistingCoreAssembly(root);
        var ns = alias switch {
            "s" => $"clr-namespace:System;assembly={core}",
            "sc" => $"clr-namespace:System.Collections;assembly={core}",
            "scg" => $"clr-namespace:System.Collections.Generic;assembly={core}",
            "sco" => $"clr-namespace:System.Collections.ObjectModel;assembly={core}",
            "sd" => "clr-namespace:System.Data;assembly=System.Data",
            _ => null
        };
        if (ns is not null) {
            root.SetAttributeValue(declaration, ns);
        }
    }

    // Prefer the assembly the document already uses for any clr-namespace: alias,
    // so a Legacy (mscorlib) workflow is not given a modern System.Private.CoreLib
    // declaration. Defaults to the modern assembly when none is present.
    private static string ExistingCoreAssembly(XElement root) {
        foreach (var attribute in root.Attributes()) {
            if (!attribute.IsNamespaceDeclaration || !attribute.Value.StartsWith("clr-namespace:System;", StringComparison.Ordinal)) {
                continue;
            }

            var assemblyIndex = attribute.Value.IndexOf("assembly=", StringComparison.Ordinal);
            if (assemblyIndex >= 0) {
                return attribute.Value[(assemblyIndex + "assembly=".Length)..];
            }
        }

        return "System.Private.CoreLib";
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
                var rewritten = RewriteReferences(doc, name, newName);
                return SurfaceEditResult.Ok(Serialize(doc), [RenameWarning("variable", name, newName, rewritten)]);
        }

        return SurfaceEditResult.Ok(Serialize(doc), []);
    }

    private static SurfaceEditResult EditArgument(
        XDocument doc, string operation, string name, string? type, string? direction, string? newName) {
        var root = doc.Root;
        if (root is null || root.Name.LocalName != "Activity") {
            return SurfaceEditResult.Failure("No root <Activity> element found in the workflow.");
        }
        var members = root.Elements(X + "Members").FirstOrDefault();
        var existing = (members?.Elements(X + "Property") ?? Enumerable.Empty<XElement>())
            .Concat(root.Elements(X + "Property"))
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
                var rewritten = RewriteReferences(doc, name, newName);
                return SurfaceEditResult.Ok(Serialize(doc), [RenameWarning("argument", name, newName, rewritten)]);
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
        var members = root.Elements(X + "Members").FirstOrDefault();
        if (members is null) {
            members = new XElement(X + "Members");
            var ownIndent = GetIndent(root);
            if (root.FirstNode is not null) {
                root.FirstNode.AddBeforeSelf(new XText("\n" + ownIndent + "  "), members);
            } else {
                root.Add(new XText("\n" + ownIndent + "  "), members, new XText("\n" + ownIndent));
            }
        }

        var lastProperty = members.Elements(X + "Property").LastOrDefault();
        var indent = GetIndent(lastProperty ?? members);
        if (lastProperty is not null) {
            lastProperty.AddAfterSelf(new XText("\n" + indent), property);
            return;
        }

        members.Add(new XText("\n" + indent + "  "), property, new XText("\n" + indent));
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

    private static string RenameWarning(string kind, string oldName, string newName, int rewrittenExpressions) =>
        rewrittenExpressions > 0
            ? $"Rename updated the {kind} declaration ('{oldName}' → '{newName}') and rewrote {rewrittenExpressions} expression reference(s) to it."
            : $"Rename updated the {kind} declaration ('{oldName}' → '{newName}'). No expression references to '{oldName}' were found.";

    // Rewrites whole-word references to a renamed declaration inside every
    // expression in the document: [bracket] attribute values, ExpressionText
    // attributes, Variable Default, and the typed argument/value elements
    // (VisualBasicValue/Reference, CSharpValue/Reference, bare InArgument text).
    // String literals are left alone. Returns the number of expressions changed.
    private static int RewriteReferences(XDocument doc, string oldName, string newName) {
        if (doc.Root is null || !IsIdentifier(oldName)) {
            return 0;
        }

        var count = 0;
        foreach (var element in doc.Root.DescendantsAndSelf()) {
            count += RewriteAttributeExpressions(element, oldName, newName);
            count += RewriteElementExpression(element, oldName, newName);
        }

        return count;
    }

    private static int RewriteAttributeExpressions(XElement element, string oldName, string newName) {
        var count = 0;
        foreach (var attribute in element.Attributes()) {
            if (attribute.IsNamespaceDeclaration) {
                continue;
            }

            var local = attribute.Name.LocalName;
            var isExpression =
                local.Equals("ExpressionText", StringComparison.Ordinal)
                || (local.Equals("Default", StringComparison.Ordinal)
                    && element.Name.LocalName.Equals("Variable", StringComparison.Ordinal))
                || IsBracketWrapped(attribute.Value);
            if (!isExpression) {
                continue;
            }

            var rewritten = ReplaceIdentifiers(attribute.Value, oldName, newName);
            if (!string.Equals(rewritten, attribute.Value, StringComparison.Ordinal)) {
                attribute.Value = rewritten;
                count++;
            }
        }

        return count;
    }

    private static int RewriteElementExpression(XElement element, string oldName, string newName) {
        var local = element.Name.LocalName;
        if (local is "VisualBasicValue" or "VisualBasicReference" or "CSharpValue" or "CSharpReference") {
            var rewritten = ReplaceIdentifiers(element.Value, oldName, newName);
            if (!string.Equals(rewritten, element.Value, StringComparison.Ordinal)) {
                element.Value = rewritten;
                return 1;
            }

            return 0;
        }

        // A bare InArgument/OutArgument/InOutArgument holds bracket text; one with
        // a child expression element is handled by that child instead.
        if (local is "InArgument" or "OutArgument" or "InOutArgument"
            && !element.HasElements && IsBracketWrapped(element.Value)) {
            var rewritten = ReplaceIdentifiers(element.Value, oldName, newName);
            if (!string.Equals(rewritten, element.Value, StringComparison.Ordinal)) {
                element.Value = rewritten;
                return 1;
            }
        }

        return 0;
    }

    // Replaces whole-word occurrences of the declaration name, skipping quoted
    // string literals and member accesses (x.oldName).
    private static string ReplaceIdentifiers(string text, string oldName, string newName) {
        var builder = new System.Text.StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length) {
            var c = text[i];
            if (c == '"') {
                builder.Append(c);
                i++;
                while (i < text.Length) {
                    builder.Append(text[i]);
                    if (text[i] == '\\' && i + 1 < text.Length) {
                        builder.Append(text[i + 1]);
                        i += 2;
                        continue;
                    }

                    if (text[i] == '"') {
                        i++;
                        break;
                    }

                    i++;
                }

                continue;
            }

            if (char.IsLetter(c) || c == '_') {
                var start = i;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) {
                    i++;
                }

                var token = text[start..i];
                var memberAccess = start > 0 && text[start - 1] == '.';
                builder.Append(!memberAccess && token.Equals(oldName, StringComparison.Ordinal) ? newName : token);
                continue;
            }

            builder.Append(c);
            i++;
        }

        return builder.ToString();
    }

    private static bool IsBracketWrapped(string? value) {
        var trimmed = value?.Trim();
        return trimmed is { Length: >= 2 } && trimmed.StartsWith('[') && trimmed.EndsWith(']');
    }

    private static bool IsIdentifier(string value) {
        if (string.IsNullOrEmpty(value) || (!char.IsLetter(value[0]) && value[0] != '_')) {
            return false;
        }

        return value.All(ch => char.IsLetterOrDigit(ch) || ch == '_');
    }

    private static string Serialize(XDocument doc) {
        EnsureTypeAliases(doc);
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
