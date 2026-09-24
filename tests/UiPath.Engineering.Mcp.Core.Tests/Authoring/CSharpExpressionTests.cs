using UiPath.Engineering.Mcp.Core.Authoring;

namespace UiPath.Engineering.Mcp.Core.Tests.Authoring;

/// <summary>
/// A CSharp project must never receive VB-only bindings: every non-literal
/// expression renders as a typed property element with CSharpValue (read) or
/// CSharpReference (write), [bracket] shorthand is rejected, attribute form is
/// kept only for values with a direct type converter, and the root declares
/// TextExpression.NamespacesForImplementation instead of VisualBasic.Settings.
/// </summary>
public class CSharpExpressionTests {
    private static readonly ProjectXamlSettings CSharp = new() { ExpressionLanguage = ExpressionLanguage.CSharp };

    private static XamlBuildResult RenderFile(ActivitySpec spec, ProjectXamlSettings? settings = null) =>
        XamlBuilder.RenderWorkflowFile(spec, "CsWorkflow", ActivityCatalog.Fallback, settings ?? CSharp);

    [Fact]
    public void BracketShorthand_IsRejectedWithALanguageSpecificFixHint() {
        var spec = new ActivitySpec {
            Name = "LogMessage",
            Properties = new() { ["message"] = "[msg]" }
        };

        var errors = SpecValidator.Validate(spec, ActivityCatalog.Fallback, CSharp);

        var error = Assert.Single(errors);
        Assert.Equal(ToolErrorCodes.SpecValueFormMismatch, error.ErrorCode);
        Assert.Contains("C#-expression project", error.Message);
        Assert.Contains("no brackets", error.FixHint);
    }

    [Fact]
    public void BracketShorthandInArgumentValues_IsRejected() {
        var spec = new ActivitySpec {
            Name = "InvokeWorkflowFile",
            Properties = new() { ["workflowFileName"] = "Child.xaml" },
            Arguments = [new ArgumentMappingSpec { Name = "in_Path", Value = "[filePath]" }]
        };

        var errors = SpecValidator.Validate(spec, ActivityCatalog.Fallback, CSharp);

        var error = Assert.Single(errors);
        Assert.Contains("bracket", error.Message);
        Assert.Contains("no brackets", error.FixHint);
    }

    [Fact]
    public void RawCSharpExpressions_AreAccepted() {
        var spec = new ActivitySpec {
            Name = "Sequence",
            Children = [
                new ActivitySpec { Name = "LogMessage", Properties = new() { ["message"] = "\"n: \" + n", ["level"] = "Info" } },
                new ActivitySpec { Name = "If", Properties = new() { ["condition"] = "count > 0" }, Children = [new ActivitySpec { Name = "Rethrow" }] },
            ]
        };

        Assert.Empty(SpecValidator.Validate(spec, ActivityCatalog.Fallback, CSharp));
        Assert.True(RenderFile(spec).Success);
    }

    [Fact]
    public void NonLiteralExpression_RendersAsTypedCSharpValuePropertyElement() {
        var spec = new ActivitySpec {
            Name = "LogMessage",
            Properties = new() { ["message"] = "\"Todo count now: \" + statusText", ["level"] = "Info" }
        };

        var result = RenderFile(spec);

        Assert.True(result.Success, string.Join(";", result.Errors.Select(e => e.Message)));
        // Message is InArgument<Object> per the binding guide — never x:String.
        Assert.Contains("<ui:LogMessage.Message>", result.Xaml);
        Assert.Contains("<InArgument x:TypeArguments=\"x:Object\">", result.Xaml);
        Assert.Contains("<CSharpValue x:TypeArguments=\"x:Object\">\"Todo count now: \" + statusText</CSharpValue>", result.Xaml);
        Assert.DoesNotContain("Message=\"", result.Xaml);
        // An enum literal stays in attribute form: it has a direct type converter.
        Assert.Contains("Level=\"Info\"", result.Xaml);
    }

    [Fact]
    public void WriteSide_RendersCSharpReferenceNotCSharpValue() {
        var spec = new ActivitySpec {
            Name = "Assign",
            Properties = new() { ["to"] = "fullName", ["value"] = "firstName + \" \" + lastName", ["typeArgument"] = "String" }
        };

        var result = RenderFile(spec);

        Assert.True(result.Success, string.Join(";", result.Errors.Select(e => e.Message)));
        Assert.Contains("<OutArgument x:TypeArguments=\"x:String\">", result.Xaml);
        Assert.Contains("<CSharpReference x:TypeArguments=\"x:String\">fullName</CSharpReference>", result.Xaml);
        Assert.Contains("<InArgument x:TypeArguments=\"x:String\">", result.Xaml);
        Assert.Contains("<CSharpValue x:TypeArguments=\"x:String\">firstName + \" \" + lastName</CSharpValue>", result.Xaml);
    }

    [Theory]
    [InlineData("00:00:02")]   // TimeSpan literal
    [InlineData("30")]         // number
    [InlineData("True")]       // boolean
    [InlineData("{x:Null}")]   // null literal
    public void ConverterLiterals_StayInAttributeForm(string duration) {
        var spec = new ActivitySpec { Name = "Delay", Properties = new() { ["duration"] = duration } };

        var result = RenderFile(spec);

        Assert.True(result.Success, string.Join(";", result.Errors.Select(e => e.Message)));
        Assert.Contains($"Duration=\"{duration}\"", result.Xaml);
        Assert.DoesNotContain("<CSharpValue", result.Xaml);
    }

    [Fact]
    public void QuotedStringLiteral_RendersThroughCSharpValueNotAttributeForm() {
        // The attribute converter does not strip C# quotes: Text=""yes"" would
        // deliver the string with its quote characters. CSharpValue evaluates it.
        var spec = new ActivitySpec { Name = "WriteLine", Properties = new() { ["text"] = "\"yes\"" } };

        var result = RenderFile(spec);

        Assert.True(result.Success, string.Join(";", result.Errors.Select(e => e.Message)));
        Assert.Contains("<WriteLine.Text>", result.Xaml);
        Assert.Contains("<CSharpValue x:TypeArguments=\"x:String\">\"yes\"</CSharpValue>", result.Xaml);
        Assert.DoesNotContain("Text=\"", result.Xaml);
    }

    [Fact]
    public void ConditionExpressions_RenderWithBooleanTyping() {
        var spec = new ActivitySpec {
            Name = "If",
            Properties = new() { ["condition"] = "count > 0" },
            Children = [new ActivitySpec { Name = "Rethrow" }]
        };

        var result = RenderFile(spec);

        Assert.True(result.Success, string.Join(";", result.Errors.Select(e => e.Message)));
        Assert.Contains("<If.Condition>", result.Xaml);
        Assert.Contains("<InArgument x:TypeArguments=\"x:Boolean\">", result.Xaml);
        Assert.Contains("<CSharpValue x:TypeArguments=\"x:Boolean\">count &gt; 0</CSharpValue>", result.Xaml);
    }

    [Fact]
    public void VariableDefault_RendersThroughVariableDefaultElement() {
        var spec = new ActivitySpec {
            Name = "Sequence",
            Variables = [new VariableSpec { Name = "name", Type = "String", Default = "\"x\"" }],
            Children = [new ActivitySpec { Name = "Rethrow" }]
        };

        var result = RenderFile(spec);

        Assert.True(result.Success, string.Join(";", result.Errors.Select(e => e.Message)));
        Assert.Contains("<Variable.Default>", result.Xaml);
        Assert.Contains("<CSharpValue x:TypeArguments=\"x:String\">\"x\"</CSharpValue>", result.Xaml);
        Assert.DoesNotContain("Default=\"", result.Xaml);
    }

    [Fact]
    public void WorkflowFile_EmitsNamespacesForImplementationNotVisualBasicSettings() {
        var result = RenderFile(new ActivitySpec { Name = "Sequence", Children = [new ActivitySpec { Name = "Rethrow" }] });

        Assert.True(result.Success, string.Join(";", result.Errors.Select(e => e.Message)));
        Assert.Contains("<TextExpression.NamespacesForImplementation>", result.Xaml);
        Assert.Contains("<sco:Collection x:TypeArguments=\"x:String\">", result.Xaml);
        Assert.Contains("<x:String>System</x:String>", result.Xaml);
        Assert.Contains("xmlns:sco=\"clr-namespace:System.Collections.ObjectModel;assembly=System.Private.CoreLib\"", result.Xaml);
        Assert.DoesNotContain("VisualBasic.Settings", result.Xaml);
    }

    [Fact]
    public void SpecImports_ReplaceTheDefaultImportSet() {
        var spec = new ActivitySpec {
            Name = "Sequence",
            Imports = ["System.Net", "MyCompany.Shared"],
            Children = [new ActivitySpec { Name = "Rethrow" }]
        };

        var result = RenderFile(spec);

        Assert.True(result.Success, string.Join(";", result.Errors.Select(e => e.Message)));
        Assert.Contains("<x:String>System.Net</x:String>", result.Xaml);
        Assert.Contains("<x:String>MyCompany.Shared</x:String>", result.Xaml);
        Assert.DoesNotContain("<x:String>System.Linq</x:String>", result.Xaml);
    }

    [Fact]
    public void VisualBasicProject_EmitsVisualBasicSettingsNotNamespacesBlock() {
        var result = XamlBuilder.RenderWorkflowFile(
            new ActivitySpec { Name = "Sequence", Children = [new ActivitySpec { Name = "Rethrow" }] },
            "VbWorkflow");

        Assert.True(result.Success, string.Join(";", result.Errors.Select(e => e.Message)));
        Assert.Contains("<VisualBasic.Settings>", result.Xaml);
        Assert.Contains("<x:Null />", result.Xaml);
        Assert.DoesNotContain("TextExpression.NamespacesForImplementation", result.Xaml);
    }

    [Fact]
    public void VisualBasicProject_IgnoresImportsWithAWarning() {
        var spec = new ActivitySpec {
            Name = "Sequence",
            Imports = ["System.Net"],
            Children = [new ActivitySpec { Name = "Rethrow" }]
        };

        var result = XamlBuilder.RenderWorkflowFile(spec, "VbWorkflow");

        Assert.True(result.Success, string.Join(";", result.Errors.Select(e => e.Message)));
        Assert.Contains("<VisualBasic.Settings>", result.Xaml);
        Assert.Contains(result.Warnings, w => w.Contains("imports", StringComparison.Ordinal));
    }

    [Fact]
    public void NoProjectContext_KeepsTodayVisualBasicBehavior() {
        var spec = new ActivitySpec {
            Name = "LogMessage",
            Properties = new() { ["message"] = "[msg]" }
        };

        var result = XamlBuilder.RenderFragment(spec);

        Assert.True(result.Success, string.Join(";", result.Errors.Select(e => e.Message)));
        Assert.Contains("Message=\"[msg]\"", result.Xaml);
        Assert.DoesNotContain("CSharpValue", result.Xaml);
    }

    [Fact]
    public void ImportsOnNonRootSpec_IsRejected() {
        var spec = new ActivitySpec {
            Name = "Sequence",
            Children = [new ActivitySpec {
                Name = "Sequence",
                Imports = ["System.Net"],
                Children = [new ActivitySpec { Name = "Rethrow" }]
            }]
        };

        var errors = SpecValidator.Validate(spec, ActivityCatalog.Fallback, CSharp);

        Assert.Contains(errors, e => e.ErrorCode == ToolErrorCodes.SpecInvalidNesting
            && e.Message.Contains("imports"));
    }

    [Fact]
    public void LegacyCSharpProject_UsesMscorlibForClrAliases() {
        var spec = new ActivitySpec {
            Name = "Sequence",
            Children = [new ActivitySpec {
                Name = "InvokeCode",
                Properties = new() { ["code"] = "x = 1;" },
                Arguments = [new ArgumentMappingSpec { Name = "x", Type = "Int32", Value = "counter" }]
            }]
        };

        var result = RenderFile(spec, new ProjectXamlSettings {
            ExpressionLanguage = ExpressionLanguage.CSharp,
            IsLegacyTargetFramework = true
        });

        Assert.True(result.Success, string.Join(";", result.Errors.Select(e => e.Message)));
        Assert.Contains("assembly=mscorlib", result.Xaml);
        Assert.DoesNotContain("System.Private.CoreLib", result.Xaml);
        // System.Data keeps its own assembly on both targets (type forwarding).
        Assert.DoesNotContain("xmlns:sd=", result.Xaml);
    }

    [Fact]
    public void ArgumentBindings_UseCSharpValueAndCSharpReferenceByDirection() {
        var spec = new ActivitySpec {
            Name = "InvokeWorkflowFile",
            Properties = new() { ["workflowFileName"] = "Child.xaml" },
            Arguments = [
                new ArgumentMappingSpec { Name = "in_Path", Direction = "In", Type = "String", Value = "filePath" },
                new ArgumentMappingSpec { Name = "out_Ok", Direction = "Out", Type = "Boolean", Value = "ok" }
            ]
        };

        var result = RenderFile(spec);

        Assert.True(result.Success, string.Join(";", result.Errors.Select(e => e.Message)));
        Assert.Contains("<InArgument x:TypeArguments=\"x:String\" x:Key=\"in_Path\">", result.Xaml);
        Assert.Contains("<CSharpValue x:TypeArguments=\"x:String\">filePath</CSharpValue>", result.Xaml);
        Assert.Contains("<OutArgument x:TypeArguments=\"x:Boolean\" x:Key=\"out_Ok\">", result.Xaml);
        Assert.Contains("<CSharpReference x:TypeArguments=\"x:Boolean\">ok</CSharpReference>", result.Xaml);
        // A plain string property (not an argument) stays an attribute literal.
        Assert.Contains("WorkflowFileName=\"Child.xaml\"", result.Xaml);
    }

    [Fact]
    public void UnknownExpressionProperty_RendersXObjectWithAWarning() {
        // A discovered (non-builtin) activity's expression property has no known
        // argument type; x:Object is assumed and the assumption is surfaced.
        var catalog = new ListActivityCatalog([
            new ActivitySchema("CustomActivity", "ui", "http://schemas.uipath.com/workflow/activities", false,
                [new PropertySchema("Payload", false, PropertyKind.Expression)])
        ], "test");

        var spec = new ActivitySpec { Name = "CustomActivity", Properties = new() { ["payload"] = "compute()" } };
        var result = XamlBuilder.RenderFragment(spec, catalog, CSharp);

        Assert.True(result.Success, string.Join(";", result.Errors.Select(e => e.Message)));
        Assert.Contains("<CSharpValue x:TypeArguments=\"x:Object\">compute()</CSharpValue>", result.Xaml);
        Assert.Contains(result.Warnings, w => w.Contains("x:Object", StringComparison.Ordinal));
    }

    [Fact]
    public void VisualBasicFixHints_StillSayVisualBasic() {
        var spec = new ActivitySpec {
            Name = "Assign",
            Properties = new() { ["to"] = "counter", ["value"] = "[counter + 1]" }
        };

        var errors = SpecValidator.Validate(spec);

        var error = Assert.Single(errors);
        Assert.Contains("wrap a VB expression in brackets", error.FixHint);
    }
}
