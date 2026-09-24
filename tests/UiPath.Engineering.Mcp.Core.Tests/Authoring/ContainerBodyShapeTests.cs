using UiPath.Engineering.Mcp.Core.Authoring;

namespace UiPath.Engineering.Mcp.Core.Tests.Authoring;

/// <summary>
/// Container bodies must render in the shape the activity actually declares:
/// .Body / .ActivityBody property elements with ActivityAction +
/// DelegateInArgument. The generic renderer used to dump children as direct
/// element children, which is well-formed XML that passes validate AND build but
/// has no valid binding slot at load time.
/// </summary>
public class ContainerBodyShapeTests {
    private static ActivitySpec Child(string name = "WriteLine") =>
        new() { Name = name, Properties = new() { ["text"] = "\"step\"" } };

    [Fact]
    public void ForEachRow_BodyUsesActivityActionWithFixedCurrentRowIterator() {
        var spec = new ActivitySpec {
            Name = "ForEachRow",
            Properties = new() { ["dataTable"] = "[dt]" },
            Children = [new ActivitySpec { Name = "LogMessage", Properties = new() { ["message"] = "[CurrentRow(0).ToString()]", ["level"] = "Info" } }]
        };

        var result = XamlBuilder.RenderFragment(spec);

        Assert.True(result.Success, string.Join(";", result.Errors.Select(e => e.Message)));
        Assert.Contains("<ui:ForEachRow.Body>", result.Xaml);
        Assert.Contains("<ActivityAction x:TypeArguments=\"sd:DataRow\">", result.Xaml);
        Assert.Contains("<ActivityAction.Argument>", result.Xaml);
        // The iterator name is fixed by the activity, not caller-chosen.
        Assert.Contains("<DelegateInArgument x:TypeArguments=\"sd:DataRow\" Name=\"CurrentRow\" />", result.Xaml);
        Assert.Contains("<Sequence DisplayName=\"Body\">", result.Xaml);
        Assert.Contains("xmlns:sd=", result.Xaml);
    }

    [Fact]
    public void RetryScope_BodyUsesActivityBodyWithUntypedActivityAction() {
        var spec = new ActivitySpec {
            Name = "RetryScope",
            Properties = new() { ["numberOfRetries"] = "3", ["retryInterval"] = "00:00:05" },
            Children = [Child()]
        };

        var result = XamlBuilder.RenderFragment(spec);

        Assert.True(result.Success, string.Join(";", result.Errors.Select(e => e.Message)));
        Assert.Contains("<ui:RetryScope.ActivityBody>", result.Xaml);
        Assert.Contains("<ActivityAction>", result.Xaml);
        Assert.Contains("<Sequence DisplayName=\"Body\">", result.Xaml);
        Assert.DoesNotContain("xmlns:sd=", result.Xaml);
    }

    [Theory]
    [InlineData("While", "<ui:InterruptibleWhile.Body>")]
    [InlineData("DoWhile", "<ui:InterruptibleDoWhile.Body>")]
    public void WhileAndDoWhile_TakeASingleSequenceWrappedBody(string activity, string bodyElement) {
        var spec = new ActivitySpec {
            Name = activity,
            Properties = new() { ["condition"] = "[n < 3]" },
            Children = [Child()]
        };

        var result = XamlBuilder.RenderFragment(spec);

        Assert.True(result.Success, string.Join(";", result.Errors.Select(e => e.Message)));
        Assert.Contains(bodyElement, result.Xaml);
        Assert.Contains("<Sequence DisplayName=\"Body\">", result.Xaml);
    }

    [Theory]
    [InlineData("While", "<ui:InterruptibleWhile")]
    [InlineData("DoWhile", "<ui:InterruptibleDoWhile")]
    public void WhileAndDoWhile_EmitTheUiPathWrapStudioEmits(string activity, string element) {
        var spec = new ActivitySpec {
            Name = activity,
            Properties = new() { ["condition"] = "[n < 3]" },
            Children = [Child()]
        };

        var result = XamlBuilder.RenderFragment(spec);

        Assert.True(result.Success, string.Join(";", result.Errors.Select(e => e.Message)));
        Assert.Contains(element, result.Xaml);
        // The framework types do not support breakpoints/step control and are no
        // longer what the catalog stamps.
        Assert.DoesNotContain("<While ", result.Xaml);
        Assert.DoesNotContain("<DoWhile ", result.Xaml);
    }

    [Fact]
    public void While_MoreThanOneChild_IsRejectedBecauseThereIsNoSecondBodySlot() {
        var spec = new ActivitySpec {
            Name = "While",
            Properties = new() { ["condition"] = "[n < 3]" },
            Children = [Child(), Child()]
        };

        var errors = SpecValidator.Validate(spec);

        var error = Assert.Single(errors, e => e.ErrorCode == ToolErrorCodes.SpecInvalidNesting);
        Assert.Contains("single Activity body", error.Message);
        Assert.Contains("Sequence", error.FixHint);
        Assert.False(XamlBuilder.RenderFragment(spec).Success);
    }

    [Fact]
    public void InvokeCode_IsNotAContainerSoChildrenAreRejected() {
        var spec = new ActivitySpec {
            Name = "InvokeCode",
            Properties = new() { ["code"] = "x = 1;" },
            Children = [Child()]
        };

        Assert.Contains(SpecValidator.Validate(spec), e => e.ErrorCode == ToolErrorCodes.SpecInvalidNesting);
        Assert.False(XamlBuilder.RenderFragment(spec).Success);
    }

    [Fact]
    public void InvokeCode_ArgumentsRenderTheDictionaryItNeverEmittedBefore() {
        var spec = new ActivitySpec {
            Name = "InvokeCode",
            Properties = new() { ["code"] = "result = input * 2;", ["language"] = "VBNet" },
            Arguments = [
                new ArgumentMappingSpec { Name = "input", Direction = "In", Type = "Int32", Value = "[n]" },
                new ArgumentMappingSpec { Name = "result", Direction = "Out", Type = "Int32", Value = "[total]" }
            ]
        };

        var result = XamlBuilder.RenderFragment(spec);

        Assert.True(result.Success, string.Join(";", result.Errors.Select(e => e.Message)));
        Assert.Contains("<ui:InvokeCode.Arguments>", result.Xaml);
        Assert.Contains("<scg:Dictionary x:TypeArguments=\"x:String, Argument\">", result.Xaml);
        Assert.Contains("<InArgument x:TypeArguments=\"x:Int32\" x:Key=\"input\">[n]</InArgument>", result.Xaml);
        Assert.Contains("<OutArgument x:TypeArguments=\"x:Int32\" x:Key=\"result\">[total]</OutArgument>", result.Xaml);
        Assert.Contains("xmlns:scg=\"clr-namespace:System.Collections.Generic;assembly=System.Private.CoreLib\"", result.Xaml);
        // No activity body: the dictionary is the only child element.
        Assert.DoesNotContain("<Sequence", result.Xaml);
    }

    [Fact]
    public void InvokeCode_RejectsAnInvalidArgumentDirection() {
        var spec = new ActivitySpec {
            Name = "InvokeCode",
            Properties = new() { ["code"] = "x = 1;" },
            Arguments = [new ArgumentMappingSpec { Name = "x", Direction = "Sideways" }]
        };

        Assert.Contains(SpecValidator.Validate(spec), e => e.ErrorCode == ToolErrorCodes.SpecValueFormMismatch);
    }

    [Fact]
    public void InvokeCode_RejectsAnArgumentWithoutAName() {
        var spec = new ActivitySpec {
            Name = "InvokeCode",
            Properties = new() { ["code"] = "x = 1;" },
            Arguments = [new ArgumentMappingSpec { Name = "  " }]
        };

        Assert.Contains(SpecValidator.Validate(spec), e => e.ErrorCode == ToolErrorCodes.SpecMissingRequiredProperty);
    }

    [Fact]
    public void InvokeCode_ChildrenOnNonContainerPointsAtTheArgumentsList() {
        var spec = new ActivitySpec {
            Name = "InvokeCode",
            Properties = new() { ["code"] = "x = 1;" },
            Children = [Child()]
        };

        var error = Assert.Single(SpecValidator.Validate(spec));
        Assert.Contains("InvokeCode takes no activity body", error.FixHint);
        Assert.Contains("arguments", error.FixHint);
    }
}
