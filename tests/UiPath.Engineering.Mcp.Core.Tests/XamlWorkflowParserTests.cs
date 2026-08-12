using UiPath.Engineering.Mcp.Core.Parsing;

namespace UiPath.Engineering.Mcp.Core.Tests;

public class XamlWorkflowParserTests {
    private const string SampleXaml = """
    <Activity x:Class="Main"
              xmlns="http://schemas.microsoft.com/netfx/2009/xaml/activities"
              xmlns:ui="http://schemas.uipath.com/workflow/activities"
              xmlns:s="clr-namespace:System;assembly=mscorlib"
              xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
      <x:Members>
        <x:Property Name="in_CustomerId" Type="InArgument(x:String)" />
        <x:Property Name="out_Result" Type="OutArgument(x:Boolean)" />
        <x:Property Name="io_Counter" Type="InOutArgument(x:Int32)" />
      </x:Members>
      <Sequence DisplayName="Main Sequence">
        <Sequence.Variables>
          <Variable x:TypeArguments="x:String" Name="userName" Default="&quot;bob&quot;" />
          <Variable x:TypeArguments="x:Int32" Name="retryCount" />
        </Sequence.Variables>
        <ui:LogMessage DisplayName="Log start" Level="Info" Message="[&quot;starting&quot;]" />
        <TryCatch DisplayName="Try risky work">
          <TryCatch.Try>
            <ui:InvokeWorkflowFile DisplayName="Invoke child" WorkflowFileName="Child.xaml" />
          </TryCatch.Try>
          <TryCatch.Catch>
            <Catch x:TypeArguments="s:Exception">
              <ActivityAction x:TypeArguments="s:Exception">
                <ui:LogMessage DisplayName="Log error" Level="Error" MessageText="[&quot;failed&quot;]" />
              </ActivityAction>
            </Catch>
          </TryCatch.Catch>
        </TryCatch>
      </Sequence>
    </Activity>
    """;

    private static Core.Models.WorkflowModel Parse(string xaml = SampleXaml) =>
        new XamlWorkflowParser().Parse("Main.xaml", "/proj/Main.xaml", xaml);

    [Fact]
    public void Parse_ExtractsArgumentsWithDirectionAndType() {
        var model = Parse();

        Assert.Equal(3, model.Arguments.Count);
        Assert.Contains(model.Arguments, a => a.Name == "in_CustomerId" && a.Direction == "In" && a.Type == "x:String");
        Assert.Contains(model.Arguments, a => a.Name == "out_Result" && a.Direction == "Out" && a.Type == "x:Boolean");
        Assert.Contains(model.Arguments, a => a.Name == "io_Counter" && a.Direction == "In/Out" && a.Type == "x:Int32");
    }

    [Fact]
    public void Parse_ExtractsVariablesWithTypeScopeAndDefault() {
        var model = Parse();

        Assert.Equal(2, model.Variables.Count);
        var userName = Assert.Single(model.Variables, v => v.Name == "userName");
        Assert.Equal("x:String", userName.Type);
        Assert.Equal("Main Sequence", userName.Scope);
        Assert.NotNull(userName.DefaultValue);
        Assert.Contains(model.Variables, v => v.Name == "retryCount" && v.Type == "x:Int32");
    }

    [Fact]
    public void Parse_BuildsFlattenedActivityOutlineWithDepth() {
        var model = Parse();

        Assert.Contains(model.Activities, a => a.Type == "Sequence" && a.DisplayName == "Main Sequence" && a.Depth == 0);
        Assert.Contains(model.Activities, a => a.Type == "LogMessage" && a.Depth == 1);
        Assert.Contains(model.Activities, a => a.Type == "TryCatch" && a.Depth == 1);
        Assert.Contains(model.Activities, a => a.Type == "InvokeWorkflowFile" && a.Depth == 2);
        // XAML infrastructure must not leak into the outline.
        Assert.DoesNotContain(model.Activities, a => a.Type is "Variable" or "Property" or "Catch" or "Collection");
    }

    [Fact]
    public void Parse_ExtractsTryCatchWithCaughtExceptionTypes() {
        var model = Parse();

        var handler = Assert.Single(model.ExceptionHandlers);
        Assert.Equal("Main.xaml", handler.WorkflowName);
        Assert.True(handler.HasGlobalHandler);
        Assert.Contains("s:Exception", handler.CatchTypes);
    }

    [Fact]
    public void Parse_ExtractsInvokeWorkflowFileTarget() {
        var model = Parse();

        var invoke = Assert.Single(model.InvokeWorkflows);
        Assert.Equal("Main.xaml", invoke.SourceWorkflow);
        Assert.Equal("Child.xaml", invoke.TargetWorkflow);
        Assert.Equal("Invoke child", invoke.DisplayName);
    }

    [Fact]
    public void Parse_ExtractsLogMessages() {
        var model = Parse();

        Assert.Equal(2, model.LogMessages.Count);
        Assert.Contains(model.LogMessages, l => l.DisplayName == "Log start" && l.Level == "Info" && l.Message.Contains("starting"));
        Assert.Contains(model.LogMessages, l => l.DisplayName == "Log error" && l.Level == "Error" && l.Message.Contains("failed"));
    }

    [Fact]
    public void Parse_MalformedXaml_ReturnsParseErrorInsteadOfThrowing() {
        var model = Parse("<Activity><Sequence></Activity>");

        Assert.True(model.HasParseError);
        Assert.NotNull(model.ParseError);
        Assert.Contains("XAML parse failure", model.ParseError);
        Assert.Equal("Main.xaml", model.FileName);
    }

    [Fact]
    public void Parse_AssignsIdsParentLinksAndOrder() {
        var model = Parse();

        var sequence = model.Activities.Single(a => a.Type == "Sequence");
        Assert.Equal("sequence.1", sequence.Id);
        Assert.Null(sequence.ParentId);

        var logStart = model.Activities.Single(a => a.DisplayName == "Log start");
        Assert.Equal("sequence.1/logmessage.1", logStart.Id);
        Assert.Equal("sequence.1", logStart.ParentId);

        var tryCatch = model.Activities.Single(a => a.Type == "TryCatch");
        Assert.Equal("sequence.1/trycatch.2", tryCatch.Id);

        var invoke = model.Activities.Single(a => a.Type == "InvokeWorkflowFile");
        Assert.Equal("sequence.1/trycatch.2/invokeworkflowfile.1", invoke.Id);
        Assert.Equal("sequence.1/trycatch.2", invoke.ParentId);

        // Log error sits under TryCatch.Catch > Catch > ActivityAction, all transparent.
        var logError = model.Activities.Single(a => a.DisplayName == "Log error");
        Assert.Equal("sequence.1/trycatch.2/logmessage.1", logError.Id);

        Assert.Equal(Enumerable.Range(0, model.Activities.Count).ToArray(),
            model.Activities.Select(a => a.Order).ToArray());
    }

    [Fact]
    public void Parse_WiresChildrenButKeepsFlatPreOrderList() {
        var model = Parse();

        var sequence = model.Activities.Single(a => a.Id == "sequence.1");
        Assert.Equal(["sequence.1/logmessage.1", "sequence.1/trycatch.2"],
            sequence.Children.Select(c => c.Id).ToArray());
        var tryCatch = model.Activities.Single(a => a.Id == "sequence.1/trycatch.2");
        Assert.Equal(["sequence.1/trycatch.2/invokeworkflowfile.1", "sequence.1/trycatch.2/logmessage.1"],
            tryCatch.Children.Select(c => c.Id).ToArray());
    }

    [Fact]
    public void Parse_ChildrenAreNotSerialized() {
        var model = Parse();

        var json = System.Text.Json.JsonSerializer.Serialize(model.Activities);

        Assert.DoesNotContain("\"Children\"", json);
        Assert.Contains("\"Id\"", json);
        Assert.Contains("\"Line\"", json);
    }

    [Fact]
    public void Parse_ReportsOneBasedLineNumbers() {
        var model = Parse();

        // SampleXaml: "<ui:LogMessage DisplayName=\"Log start\" ... />" is content line 16 of the raw literal.
        var logStart = model.Activities.Single(a => a.DisplayName == "Log start");
        Assert.Equal(16, logStart.Line);
        Assert.All(model.Activities, a => Assert.True(a.Line > 0));
    }
}
