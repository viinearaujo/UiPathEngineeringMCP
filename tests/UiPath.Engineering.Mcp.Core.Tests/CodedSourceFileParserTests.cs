using UiPath.Engineering.Mcp.Core.Parsing;

namespace UiPath.Engineering.Mcp.Core.Tests;

public class CodedSourceFileParserTests {
    private const string CodedWorkflow = """
        using System;
        using UiPath.CodedWorkflows;

        namespace MyTest_Project
        {
            public class InvoiceFlow : CodedWorkflow
            {
                [Workflow]
                public void Execute()
                {
                }

                public int CalculateTotal(int a, int b)
                {
                    return a + b;
                }

                private void Helper()
                {
                }
            }
        }
        """;

    private const string PlainSource = """
        using System;

        namespace MyTest_Project
        {
            public class Helpers
            {
                public static string Format(string value)
                {
                    return value;
                }
            }
        }
        """;

    [Fact]
    public void Parse_CodedWorkflow_ExtractsClassNamespaceAndEntryMethod() {
        var model = new CodedSourceFileParser().Parse("InvoiceFlow.cs", "/p/InvoiceFlow.cs", CodedWorkflow);

        Assert.False(model.HasParseError);
        Assert.Equal("InvoiceFlow", model.ClassName);
        Assert.Equal("MyTest_Project", model.Namespace);
        Assert.True(model.IsCodedWorkflow);
        Assert.Equal(["Execute"], model.EntryMethods);
    }

    [Fact]
    public void Parse_CodedWorkflow_PublicMethodsExcludeEntryMethodsPrivatesAndConstructors() {
        const string content = """
            public class InvoiceFlow : CodedWorkflow
            {
                public InvoiceFlow() { }

                [Workflow]
                public void Execute() { }

                public async Task RunAsync() { }

                public static int Add(int a, int b) { return a + b; }

                private void Hidden() { }
            }
            """;

        var model = new CodedSourceFileParser().Parse("InvoiceFlow.cs", "/p/InvoiceFlow.cs", content);

        Assert.Equal(["RunAsync", "Add"], model.PublicMethods);
    }

    [Fact]
    public void Parse_PlainSourceFile_IsNotCodedWorkflowAndHasNoEntryMethods() {
        var model = new CodedSourceFileParser().Parse("Helpers.cs", "/p/Helpers.cs", PlainSource);

        Assert.False(model.HasParseError);
        Assert.False(model.IsCodedWorkflow);
        Assert.Empty(model.EntryMethods);
        Assert.Equal(["Format"], model.PublicMethods);
    }

    [Fact]
    public void Parse_WorkflowAttributeWithArguments_StillDetectsEntryMethod() {
        const string content = """
            public class Flow : CodedWorkflow
            {
                [Workflow]
                public void Run() { }
            }
            """;

        var model = new CodedSourceFileParser().Parse("Flow.cs", "/p/Flow.cs", content);

        Assert.True(model.IsCodedWorkflow);
        Assert.Equal(["Run"], model.EntryMethods);
    }

    [Fact]
    public void Parse_GarbageContent_ReportsParseErrorWithoutThrowing() {
        var model = new CodedSourceFileParser().Parse("Broken.cs", "/p/Broken.cs", "this is not c# at all {{{{");

        Assert.True(model.HasParseError);
        Assert.NotNull(model.ParseError);
        Assert.Equal("Broken.cs", model.FileName);
    }

    [Fact]
    public void Parse_EmptyContent_ReportsParseErrorWithoutThrowing() {
        var model = new CodedSourceFileParser().Parse("Empty.cs", "/p/Empty.cs", "   ");

        Assert.True(model.HasParseError);
        Assert.NotNull(model.ParseError);
    }
}
