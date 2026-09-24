using System.Text.Json;
using UiPath.Engineering.Mcp.Core;
using UiPath.Engineering.Mcp.Core.Authoring;
using UiPath.Engineering.Mcp.Core.Parsing;

namespace UiPath.Engineering.Mcp.Core.Tests.Authoring;

public class XamlBuilderTests {
    [Fact]
    public void RenderFragment_Assign_RendersExpressionAttributes() {
        var spec = new ActivitySpec {
            Name = "Assign",
            Properties = new() { ["to"] = "[counter]", ["value"] = "[counter + 1]" }
        };
        var result = XamlBuilder.RenderFragment(spec);
        Assert.True(result.Success);
        Assert.Contains("<Assign", result.Xaml);
        Assert.Contains("To=\"[counter]\"", result.Xaml);
        Assert.Contains("Value=\"[counter + 1]\"", result.Xaml);
    }

    [Fact]
    public void RenderFragment_ForEach_RendersActivityActionShape() {
        var spec = new ActivitySpec {
            Name = "ForEach",
            Properties = new() { ["values"] = "[rows]", ["typeArgument"] = "DataRow", ["itemName"] = "row" },
            Children = [new ActivitySpec { Name = "LogMessage", Properties = new() { ["message"] = "[row(0).ToString()]" } }]
        };
        var result = XamlBuilder.RenderFragment(spec);
        // Studio's For Each toolbox item emits ui:ForEach, not the framework type.
        Assert.Contains("<ui:ForEach x:TypeArguments=\"sd:DataRow\"", result.Xaml);
        Assert.Contains("<DelegateInArgument x:TypeArguments=\"sd:DataRow\" Name=\"row\" />", result.Xaml);
        Assert.Contains("<ui:LogMessage", result.Xaml);
        // The declared alias must resolve: System.Data types are not in the x: schema.
        Assert.Contains("xmlns:sd=\"clr-namespace:System.Data;assembly=System.Data\"", result.Xaml);
        Assert.Contains("xmlns:ui=\"http://schemas.uipath.com/workflow/activities\"", result.Xaml);
    }

    [Fact]
    public void RenderWorkflowFile_DesignDocExample_RoundTripsThroughParser() {
        // deserialize the design-doc example JSON (same literal as Task 2 test)
        const string json = """
        { "name": "Sequence",
          "variables": [{ "name": "rowCount", "type": "Int32", "default": "0" }],
          "children": [
            { "name": "ForEach",
              "properties": { "values": "[in_TransactionData]", "typeArgument": "DataRow" },
              "children": [
                { "name": "TryCatch",
                  "children": [ { "name": "LogMessage", "properties": { "message": "\"Processing row\"", "level": "Info" } } ],
                  "catches": [ { "exception": "System.Exception", "children": [ { "name": "Rethrow" } ] } ] } ] } ] }
        """;
        var spec = JsonSerializer.Deserialize<ActivitySpec>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var result = XamlBuilder.RenderWorkflowFile(spec, "TestWorkflow");
        Assert.True(result.Success, string.Join(";", result.Errors.Select(e => e.Message)));
        Assert.Contains("x:Class=\"TestWorkflow\"", result.Xaml);
        Assert.Contains("<TryCatch ", result.Xaml);
        Assert.Contains("<TryCatch.Catches>", result.Xaml);
        Assert.Contains("<Catch x:TypeArguments=\"System.Exception\">", result.Xaml);
        // Every activity carries the designer handle ValidateDiagnosticMapper reads.
        Assert.Contains("sap2010:WorkflowViewState.IdRef=\"TryCatch_1\"", result.Xaml);
        Assert.Contains("sap:VirtualizedContainerService.HintSize=", result.Xaml);
        // round-trip is asserted inside RenderWorkflowFile itself; reaching Success proves it
    }

    [Fact]
    public void RenderWorkflowFile_VariableTypes_ResolveToValidPrefixes() {
        var spec = new ActivitySpec {
            Name = "Sequence",
            Variables = [new VariableSpec { Name = "row", Type = "DataRow" },
                new VariableSpec { Name = "n", Type = "Int32" },
                new VariableSpec { Name = "when", Type = "DateTime" }]
        };
        var result = XamlBuilder.RenderWorkflowFile(spec, "TestWorkflow");
        Assert.True(result.Success, string.Join(";", result.Errors.Select(e => e.Message)));
        // DataRow is not an x: schema primitive; it must resolve through sd:, never x:DataRow.
        Assert.Contains("<Variable x:TypeArguments=\"sd:DataRow\" Name=\"row\" />", result.Xaml);
        Assert.Contains("<Variable x:TypeArguments=\"x:Int32\" Name=\"n\" />", result.Xaml);
        Assert.Contains("<Variable x:TypeArguments=\"s:DateTime\" Name=\"when\" />", result.Xaml);
        Assert.Contains("xmlns:sd=\"clr-namespace:System.Data;assembly=System.Data\"", result.Xaml);
        Assert.Contains("xmlns:s=\"clr-namespace:System;assembly=System.Private.CoreLib\"", result.Xaml);
        Assert.DoesNotContain("x:DataRow", result.Xaml);
        Assert.DoesNotContain("x:DateTime", result.Xaml);
    }

    [Fact]
    public void RenderWorkflowFile_InvalidSpec_ShortCircuitsWithValidatorErrors() {
        var spec = new ActivitySpec {
            Name = "Assign",
            Properties = new() { ["to"] = "[x]" }
        }; // missing required "value"
        var result = XamlBuilder.RenderWorkflowFile(spec, "TestWorkflow");
        Assert.False(result.Success);
        Assert.Null(result.Xaml);
        Assert.Contains(result.Errors, e => e.ErrorCode == ToolErrorCodes.SpecMissingRequiredProperty);
    }

    [Fact]
    public void RenderFragment_If_RendersThenBranchShape() {
        var spec = new ActivitySpec {
            Name = "If",
            Properties = new() { ["condition"] = "[counter > 0]" },
            Children = [new ActivitySpec { Name = "WriteLine", Properties = new() { ["text"] = "\"positive\"" } }]
        };
        var result = XamlBuilder.RenderFragment(spec);
        Assert.True(result.Success);
        Assert.Contains("<If Condition=\"[counter &gt; 0]\"", result.Xaml);
        Assert.Contains("<If.Then>", result.Xaml);
        Assert.Contains("<WriteLine", result.Xaml);
        Assert.DoesNotContain("<If.Else>", result.Xaml);
    }

    [Fact]
    public void RenderFragment_If_RendersElseBranch() {
        var spec = new ActivitySpec {
            Name = "If",
            Properties = new() { ["condition"] = "[counter > 0]" },
            Children = [new ActivitySpec { Name = "WriteLine", Properties = new() { ["text"] = "\"positive\"" } }],
            Else = [new ActivitySpec { Name = "WriteLine", Properties = new() { ["text"] = "\"non-positive\"" } }]
        };
        var result = XamlBuilder.RenderFragment(spec);
        Assert.True(result.Success, string.Join(";", result.Errors.Select(e => e.Message)));
        Assert.Contains("<If.Then>", result.Xaml);
        Assert.Contains("<If.Else>", result.Xaml);
        Assert.Contains("positive", result.Xaml);
        Assert.Contains("non-positive", result.Xaml);
    }

    [Fact]
    public void RenderFragment_Switch_RendersCasesAndDefault() {
        var spec = new ActivitySpec {
            Name = "Switch",
            Properties = new() { ["expression"] = "[status]", ["typeArgument"] = "Int32" },
            Cases =
            [
                new SwitchCaseSpec { Key = "1", Children = [new ActivitySpec { Name = "WriteLine", Properties = new() { ["text"] = "\"one\"" } }] },
                new SwitchCaseSpec { Key = "2", Children = [new ActivitySpec { Name = "LogMessage", Properties = new() { ["message"] = "\"two\"" } }] }
            ],
            Default = [new ActivitySpec { Name = "Rethrow" }]
        };
        var result = XamlBuilder.RenderFragment(spec);
        Assert.True(result.Success, string.Join(";", result.Errors.Select(e => e.Message)));
        Assert.Contains("<Switch x:TypeArguments=\"x:Int32\"", result.Xaml);
        Assert.Contains("Expression=\"[status]\"", result.Xaml);
        Assert.Contains("x:Key=\"1\"", result.Xaml);
        Assert.Contains("x:Key=\"2\"", result.Xaml);
        Assert.Contains("<Switch.Default>", result.Xaml);
        Assert.Contains("<Rethrow", result.Xaml);
        Assert.Contains("<ui:LogMessage", result.Xaml);
    }

    [Fact]
    public void RenderFragment_InvokeWorkflowFile_RendersArgumentMappings() {
        var spec = new ActivitySpec {
            Name = "InvokeWorkflowFile",
            Properties = new() { ["workflowFileName"] = "Child.xaml" },
            Arguments =
            [
                new ArgumentMappingSpec { Name = "in_Path", Direction = "In", Type = "String", Value = "[filePath]" },
                new ArgumentMappingSpec { Name = "out_Ok", Direction = "Out", Type = "Boolean", Value = "[ok]" }
            ]
        };
        var result = XamlBuilder.RenderFragment(spec);
        Assert.True(result.Success, string.Join(";", result.Errors.Select(e => e.Message)));
        Assert.Contains("WorkflowFileName=\"Child.xaml\"", result.Xaml);
        Assert.Contains("InvokeWorkflowFile.Arguments", result.Xaml);
        Assert.Contains("x:Key=\"in_Path\"", result.Xaml);
        Assert.Contains("[filePath]", result.Xaml);
        Assert.Contains("x:Key=\"out_Ok\"", result.Xaml);
        Assert.Contains("xmlns:scg=", result.Xaml);
    }

    [Fact]
    public void RenderWorkflowFile_VariablesOnNonSequenceRoot_WrapsInOuterSequence() {
        var spec = new ActivitySpec {
            Name = "LogMessage",
            Properties = new() { ["message"] = "\"hi\"" },
            Variables = [new VariableSpec { Name = "n", Type = "Int32" }]
        };
        var result = XamlBuilder.RenderWorkflowFile(spec, "TestWorkflow");
        Assert.True(result.Success, string.Join(";", result.Errors.Select(e => e.Message)));
        Assert.Contains("<Sequence.Variables>", result.Xaml);
        Assert.Contains("<Variable x:TypeArguments=\"x:Int32\" Name=\"n\" />", result.Xaml);
    }

    [Fact]
    public void RenderWorkflowFile_WorkflowArguments_RenderMembersProperties() {
        var spec = new ActivitySpec {
            Name = "Sequence",
            WorkflowArguments = [
                new ArgumentSpec { Name = "in_Path", Type = "String", Direction = "In" },
                new ArgumentSpec { Name = "out_Ok", Type = "Boolean", Direction = "Out" },
                new ArgumentSpec { Name = "io_Table", Type = "DataTable", Direction = "InOut" }
            ],
            Children = [new ActivitySpec { Name = "WriteLine", Properties = new() { ["text"] = "\"hi\"" } }]
        };
        var result = XamlBuilder.RenderWorkflowFile(spec, "TestWorkflow");
        Assert.True(result.Success, string.Join(";", result.Errors.Select(e => e.Message)));
        Assert.Contains("<x:Members>", result.Xaml);
        Assert.Contains("<x:Property Name=\"in_Path\" Type=\"InArgument(x:String)\" />", result.Xaml);
        Assert.Contains("<x:Property Name=\"out_Ok\" Type=\"OutArgument(x:Boolean)\" />", result.Xaml);
        Assert.Contains("<x:Property Name=\"io_Table\" Type=\"InOutArgument(sd:DataTable)\" />", result.Xaml);
        Assert.Contains("xmlns:sd=", result.Xaml);
    }

    [Fact]
    public void RenderWorkflowFile_NoWorkflowArguments_OmitsMembers() {
        var spec = new ActivitySpec {
            Name = "Sequence",
            Children = [new ActivitySpec { Name = "WriteLine", Properties = new() { ["text"] = "\"hi\"" } }]
        };
        var result = XamlBuilder.RenderWorkflowFile(spec, "TestWorkflow");
        Assert.True(result.Success, string.Join(";", result.Errors.Select(e => e.Message)));
        Assert.DoesNotContain("<x:Members>", result.Xaml);
    }

    [Fact]
    public void RenderWorkflowFile_WorkflowArgumentsOnNestedSpec_IsRejected() {
        var spec = new ActivitySpec {
            Name = "Sequence",
            Children = [new ActivitySpec {
                Name = "Sequence",
                WorkflowArguments = [new ArgumentSpec { Name = "in_X", Type = "String" }]
            }]
        };
        Assert.Contains(SpecValidator.Validate(spec), e => e.ErrorCode == ToolErrorCodes.SpecInvalidNesting);
    }

    [Fact]
    public void RenderFragment_NApplicationCard_RendersBodyWithUixPrefix() {
        var spec = new ActivitySpec {
            Name = "NApplicationCard",
            Properties = new() { ["interactionMode"] = "HardwareEvents" },
            Children = [
                new ActivitySpec { Name = "NClick", Properties = new() { ["displayName"] = "TODO Indicate — Click Login" } },
                new ActivitySpec { Name = "NTypeInto", Properties = new() { ["text"] = "[user]", ["displayName"] = "TODO Indicate — Type Username" } }
            ]
        };

        var result = XamlBuilder.RenderFragment(spec);

        Assert.True(result.Success, string.Join(";", result.Errors.Select(e => e.Message)));
        Assert.Contains("<uix:NApplicationCard", result.Xaml);
        Assert.Contains("<uix:NApplicationCard.Body>", result.Xaml);
        Assert.Contains("<uix:NClick", result.Xaml);
        Assert.Contains("<uix:NTypeInto", result.Xaml);
        Assert.Contains("xmlns:uix=\"http://schemas.uipath.com/workflow/activities/uix\"", result.Xaml);
        // A uix: element must never serialize under a generated d1p1: prefix.
        Assert.DoesNotContain("d1p1:", result.Xaml);
    }

    [Fact]
    public void RenderFragment_NGetText_RendersOutputExpressionAsAPropertyElementInCSharp() {
        var spec = new ActivitySpec {
            Name = "NGetText",
            Properties = new() { ["textString"] = "statusText" }
        };

        var result = XamlBuilder.RenderFragment(spec, ActivityCatalog.Fallback,
            new ProjectXamlSettings { ExpressionLanguage = ExpressionLanguage.CSharp });

        Assert.True(result.Success, string.Join(";", result.Errors.Select(e => e.Message)));
        Assert.Contains("<uix:NGetText.TextString>", result.Xaml);
        Assert.Contains("<OutArgument x:TypeArguments=\"x:String\">", result.Xaml);
        Assert.Contains("<CSharpReference x:TypeArguments=\"x:String\">statusText</CSharpReference>", result.Xaml);
    }

    [Fact]
    public void RenderFragment_ExcelApplicationCard_RendersTypedBodyAndAliases() {
        var spec = new ActivitySpec {
            Name = "ExcelApplicationCard",
            Properties = new() { ["workbookPath"] = "\"book.xlsx\"" },
            Children = [new ActivitySpec { Name = "ReadRangeX", Properties = new() { ["range"] = "\"A1:B2\"", ["saveTo"] = "[dt]" } }]
        };

        var result = XamlBuilder.RenderFragment(spec);

        Assert.True(result.Success, string.Join(";", result.Errors.Select(e => e.Message)));
        Assert.Contains("<ueab:ExcelApplicationCard", result.Xaml);
        Assert.Contains("<ueab:ExcelApplicationCard.Body>", result.Xaml);
        Assert.Contains("<ActivityAction x:TypeArguments=\"ue:IWorkbookQuickHandle\">", result.Xaml);
        Assert.Contains("<DelegateInArgument x:TypeArguments=\"ue:IWorkbookQuickHandle\" Name=\"Excel\" />", result.Xaml);
        Assert.Contains("xmlns:ueab=\"clr-namespace:UiPath.Excel.Activities.Business;assembly=UiPath.Excel.Activities\"", result.Xaml);
        Assert.Contains("xmlns:ue=\"clr-namespace:UiPath.Excel;assembly=UiPath.Excel.Activities\"", result.Xaml);
    }

    [Fact]
    public void RenderFragment_ForEach_ItemNameOverrideWinsOverTheDescriptorDefault() {
        var spec = new ActivitySpec {
            Name = "ForEach",
            Properties = new() { ["values"] = "[rows]", ["typeArgument"] = "String", ["itemName"] = "row" },
            Children = [new ActivitySpec { Name = "WriteLine", Properties = new() { ["text"] = "[row]" } }]
        };

        var result = XamlBuilder.RenderFragment(spec);

        Assert.True(result.Success, string.Join(";", result.Errors.Select(e => e.Message)));
        Assert.Contains("<DelegateInArgument x:TypeArguments=\"x:String\" Name=\"row\" />", result.Xaml);
    }

    [Fact]
    public void RenderFragment_Annotation_RendersSap2010AnnotationText() {
        var spec = new ActivitySpec {
            Name = "Sequence",
            Annotation = "Reads the invoice queue",
            Children = [new ActivitySpec {
                Name = "WriteLine",
                Properties = new() { ["text"] = "\"hi\"" },
                Annotation = "Diagnostic echo"
            }]
        };

        var result = XamlBuilder.RenderFragment(spec);

        Assert.True(result.Success, string.Join(";", result.Errors.Select(e => e.Message)));
        Assert.Contains("sap2010:Annotation.AnnotationText=\"Reads the invoice queue\"", result.Xaml);
        Assert.Contains("sap2010:Annotation.AnnotationText=\"Diagnostic echo\"", result.Xaml);
        Assert.Contains("xmlns:sap2010=\"http://schemas.microsoft.com/netfx/2010/xaml/activities/presentation\"", result.Xaml);
    }

    [Fact]
    public void RenderWorkflowFile_RootAnnotation_LandsOnTheRootActivityAndPopulatesDescription() {
        // The workflow annotation belongs on the root ACTIVITY element (where Studio
        // writes it), not on the <Activity> wrapper — and it must read back as
        // WorkflowModel.Description.
        var spec = new ActivitySpec {
            Name = "Sequence",
            Annotation = "Reads the invoice queue.",
            Children = [new ActivitySpec { Name = "WriteLine", Properties = new() { ["text"] = "\"hi\"" } }]
        };

        var result = XamlBuilder.RenderWorkflowFile(spec, "Workflows_Read");

        Assert.True(result.Success, string.Join(";", result.Errors.Select(e => e.Message)));
        var doc = System.Xml.Linq.XDocument.Parse(result.Xaml!);
        var activity = doc.Root!;
        Assert.Null(activity.Attribute(XamlViewStateEmitter.Sap2010 + "Annotation.AnnotationText"));
        var rootSequence = activity.Elements().Single(e => e.Name.LocalName == "Sequence");
        Assert.Equal("Reads the invoice queue.",
            rootSequence.Attribute(XamlViewStateEmitter.Sap2010 + "Annotation.AnnotationText")!.Value);

        // The parser surfaces it as the workflow description, so gap analysis and
        // generated docs no longer see an undocumented workflow that visibly has one.
        var model = new XamlWorkflowParser().Parse("Workflows_Read.xaml", "Workflows_Read.xaml", result.Xaml!);
        Assert.Equal("Reads the invoice queue.", model.Description);
    }

    [Fact]
    public void RenderWorkflowFile_NoAnnotation_DeclaresNoSap2010InFragment() {
        var spec = new ActivitySpec {
            Name = "Sequence",
            Children = [new ActivitySpec { Name = "WriteLine", Properties = new() { ["text"] = "\"hi\"" } }]
        };

        var result = XamlBuilder.RenderFragment(spec);

        Assert.True(result.Success, string.Join(";", result.Errors.Select(e => e.Message)));
        Assert.DoesNotContain("sap2010:", result.Xaml);
    }

    [Fact]
    public void RenderFragment_NestedSequence_MayDeclareItsOwnVariables() {
        var spec = new ActivitySpec {
            Name = "Sequence",
            Children = [new ActivitySpec {
                Name = "Sequence",
                Variables = [new VariableSpec { Name = "local", Type = "Int32" }],
                Children = [new ActivitySpec { Name = "WriteLine", Properties = new() { ["text"] = "[local]" } }]
            }]
        };

        Assert.Empty(SpecValidator.Validate(spec));
        var result = XamlBuilder.RenderFragment(spec);

        Assert.True(result.Success, string.Join(";", result.Errors.Select(e => e.Message)));
        Assert.Contains("<Sequence.Variables>", result.Xaml);
        Assert.Contains("<Variable x:TypeArguments=\"x:Int32\" Name=\"local\" />", result.Xaml);
    }

    [Fact]
    public void Validate_VariablesOnNonSequence_IsRejected() {
        var spec = new ActivitySpec {
            Name = "Sequence",
            Children = [new ActivitySpec {
                Name = "If",
                Properties = new() { ["condition"] = "[flag]" },
                Variables = [new VariableSpec { Name = "v", Type = "Int32" }]
            }]
        };

        var error = Assert.Single(SpecValidator.Validate(spec), e => e.ErrorCode == ToolErrorCodes.SpecInvalidNesting);
        Assert.Contains("Sequence", error.FixHint);
    }
}
