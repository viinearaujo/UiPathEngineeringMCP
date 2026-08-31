using System.Text.RegularExpressions;
using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Core.Parsing;

/// <summary>
/// Dependency-free (regex/line-based) extraction of the structure of a UiPath coded
/// workflow / coded test case / coded source .cs file: namespace, class name + base
/// types, kind, [Workflow]/[TestCase] entry methods with parameter types, and public
/// method names. Never throws on bad input; unparseable files come back with
/// <see cref="CodedWorkflowModel.HasParseError"/>, mirroring
/// <see cref="XamlWorkflowParser"/>.
/// </summary>
public sealed class CodedSourceFileParser {
    private static readonly Regex NamespacePattern = new(
        @"^\s*namespace\s+([A-Za-z_][\w.]*)",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex ClassPattern = new(
        @"\bclass\s+([A-Za-z_]\w*)\s*(?::\s*(?<bases>[^\{]+?))?\s*\{",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex AttributeMethodPattern = new(
        @"\[\s*(?<attr>Workflow|TestCase)(?:\([^\]]*\))?\s*\]\s*(?:\[[^\]]*\]\s*)*(?:public|private|protected|internal|static|async|virtual|override|sealed|partial|\s)*[\w<>\[\],.?]+\s+(?<name>[A-Za-z_]\w*)\s*\((?<params>[^)]*)\)",
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

        var attributed = AttributeMethodPattern.Matches(content);
        var hasTestCase = false;
        var hasWorkflow = false;
        foreach (Match match in attributed) {
            var attr = match.Groups["attr"].Value;
            var name = match.Groups["name"].Value;
            if (!model.EntryMethods.Contains(name)) {
                model.EntryMethods.Add(name);
            }

            if (attr.Equals("TestCase", StringComparison.OrdinalIgnoreCase)) {
                hasTestCase = true;
            } else {
                hasWorkflow = true;
            }

            if (model.EntryArguments.Count == 0) {
                model.EntryArguments.AddRange(ParseParameters(match.Groups["params"].Value));
            }
        }

        if (hasTestCase) {
            model.Kind = CodedFileKind.Test;
        } else if (model.IsCodedWorkflow || hasWorkflow) {
            model.Kind = CodedFileKind.Workflow;
        } else {
            model.Kind = CodedFileKind.Source;
        }

        model.PublicMethods.AddRange(PublicMethodPattern.Matches(content)
            .Select(m => m.Groups[1].Value)
            // Constructors match the method pattern; entry methods get their own list.
            .Where(name => name != model.ClassName)
            .Distinct()
            .Except(model.EntryMethods));

        return model;
    }

    internal static List<ArgumentModel> ParseParameters(string raw) {
        if (string.IsNullOrWhiteSpace(raw)) {
            return [];
        }

        var args = new List<ArgumentModel>();
        foreach (var part in SplitTopLevel(raw, ',')) {
            var tokens = part.Trim();
            var eq = tokens.IndexOf('=');
            if (eq >= 0) {
                tokens = tokens[..eq].Trim();
            }

            while (tokens.StartsWith('[')) {
                var close = tokens.IndexOf(']');
                if (close < 0) {
                    break;
                }

                tokens = tokens[(close + 1)..].Trim();
            }

            var lastSpace = tokens.LastIndexOf(' ');
            if (lastSpace < 0) {
                continue;
            }

            var type = tokens[..lastSpace].Trim();
            var name = tokens[(lastSpace + 1)..].Trim().TrimStart('@');
            if (name.Length == 0 || type.Length == 0) {
                continue;
            }

            args.Add(new ArgumentModel { Name = name, Type = type, Direction = "In" });
        }

        return args;
    }

    private static IEnumerable<string> SplitTopLevel(string value, char separator) {
        var depth = 0;
        var start = 0;
        for (var i = 0; i < value.Length; i++) {
            var c = value[i];
            if (c is '<' or '(') {
                depth++;
            } else if (c is '>' or ')') {
                depth = Math.Max(0, depth - 1);
            } else if (c == separator && depth == 0) {
                yield return value[start..i];
                start = i + 1;
            }
        }

        yield return value[start..];
    }
}
