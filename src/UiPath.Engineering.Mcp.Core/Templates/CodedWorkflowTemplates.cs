namespace UiPath.Engineering.Mcp.Core.Templates;

// Templates for UiPath Coded Workflows, coded test cases, and plain coded source files.
// Rules (from UiPath's coding-agent skills): a coded workflow inherits CodedWorkflow
// and marks its entry method with [Workflow]; a coded test case inherits CodedWorkflow
// and marks its entry method with [TestCase]; one class per file, class name == file name,
// namespace = sanitized project name. Helper/source files must NOT inherit
// CodedWorkflow and carry no [Workflow] or [TestCase] attribute.
public static class CodedWorkflowTemplates {
    public static string CodedWorkflow(string namespaceName, string className) => $$"""
        using System;
        using System.Collections.Generic;
        using UiPath.CodedWorkflows;

        namespace {{namespaceName}}
        {
            public class {{className}} : CodedWorkflow
            {
                [Workflow]
                public void Execute()
                {
                    // TODO: implement the workflow.
                }
            }
        }
        """;

    public static string CodedTestCase(string namespaceName, string className) => $$"""
        using System;
        using System.Collections.Generic;
        using UiPath.CodedWorkflows;

        namespace {{namespaceName}}
        {
            public class {{className}} : CodedWorkflow
            {
                [TestCase]
                public void Execute()
                {
                    // Arrange

                    // Act

                    // Assert
                }
            }
        }
        """;

    public static string CodedSourceFile(string namespaceName, string className) => $$"""
        using System;
        using System.Collections.Generic;

        namespace {{namespaceName}}
        {
            public class {{className}}
            {
                // TODO: add members.
            }
        }
        """;

    // UiPath convention: namespace = project name with spaces removed, hyphens and
    // other invalid characters replaced by '_', and a leading '_' if it would
    // otherwise start with a digit.
    public static string SanitizeNamespace(string projectName) {
        var chars = projectName
            .Where(c => !char.IsWhiteSpace(c))
            .Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_')
            .ToArray();
        var ns = new string(chars);
        if (ns.Length == 0) {
            return "UiPathProject";
        }
        return char.IsDigit(ns[0]) ? "_" + ns : ns;
    }

    public static bool IsValidClassName(string className) =>
        !string.IsNullOrWhiteSpace(className)
        && (char.IsLetter(className[0]) || className[0] == '_')
        && className.All(c => char.IsLetterOrDigit(c) || c == '_');
}
