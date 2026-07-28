using System.Text.RegularExpressions;
using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Core.Parsing;

/// <summary>
/// Dependency-free (regex/line-based) extraction of the structure of a UiPath coded
/// workflow / coded source .cs file: namespace, class name + base types, [Workflow]
/// entry methods, and public method names. Never throws on bad input; unparseable
/// files come back with <see cref="CodedWorkflowModel.HasParseError"/>, mirroring
/// <see cref="XamlWorkflowParser"/>.
/// </summary>
public sealed class CodedSourceFileParser {
    private static readonly Regex NamespacePattern = new(
        @"^\s*namespace\s+([A-Za-z_][\w.]*)",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex ClassPattern = new(
        @"\bclass\s+([A-Za-z_]\w*)\s*(?::\s*(?<bases>[^\{]+?))?\s*\{",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex WorkflowMethodPattern = new(
        @"\[\s*Workflow(?:\([^\]]*\))?\s*\]\s*(?:\[[^\]]*\]\s*)*(?:public|private|protected|internal|static|async|virtual|override|sealed|partial|\s)*[\w<>\[\],.?]+\s+([A-Za-z_]\w*)\s*\(",
        RegexOptions.Compiled);

    private static readonly Regex PublicMethodPattern = new(
        @"^\s*public\s+(?:static\s+|async\s+|virtual\s+|override\s+|sealed\s+|partial\s+)*[\w<>\[\],.?]+\s+([A-Za-z_]\w*)\s*\(",
        RegexOptions.Multiline | RegexOptions.Compiled);

    public CodedWorkflowModel Parse(string fileName, string filePath, string content) {
        var model = new CodedWorkflowModel { FileName = fileName, FilePath = filePath };
        if (string.IsNullOrWhiteSpace(content)) {
            model.HasParseError = true;
            model.ParseError = "C# parse failure: file is empty.";
            return model;
        }

        model.Namespace = NamespacePattern.Match(content) is { Success: true } ns ? ns.Groups[1].Value : string.Empty;

        var classMatch = ClassPattern.Match(content);
        if (!classMatch.Success) {
            model.HasParseError = true;
            model.ParseError = "C# parse failure: no class declaration found.";
            return model;
        }

        model.ClassName = classMatch.Groups[1].Value;
        var baseTypes = classMatch.Groups["bases"].Success
            ? classMatch.Groups["bases"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];
        model.IsCodedWorkflow = baseTypes.Any(b => b.Split('.').Last() == "CodedWorkflow");

        model.EntryMethods.AddRange(WorkflowMethodPattern.Matches(content)
            .Select(m => m.Groups[1].Value)
            .Distinct());

        model.PublicMethods.AddRange(PublicMethodPattern.Matches(content)
            .Select(m => m.Groups[1].Value)
            // Constructors match the method pattern; entry methods get their own list.
            .Where(name => name != model.ClassName)
            .Distinct()
            .Except(model.EntryMethods));

        return model;
    }
}
