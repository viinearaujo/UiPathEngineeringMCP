using UiPath.Engineering.Mcp.Core.Authoring;

namespace UiPath.Engineering.Mcp.Core.Tests.Authoring;

/// <summary>
/// Rule 24 (uipath-rpa SKILL.md): every container body/branch slot holds a
/// <see cref="object">Sequence</see>, even a single-activity one. validate and
/// build both accept the bare form, so the green gate cannot catch a missing
/// wrap — these tests are the only thing that does.
/// </summary>
public class Rule24SequenceWrapTests {
    private static ActivitySpec Leaf(string text = "step") =>
        new() { Name = "WriteLine", Properties = new() { ["text"] = $"\"{text}\"" } };

    [Fact]
    public void If_ThenAndElse_AreSequenceWrappedEvenForASingleChild() {
        var spec = new ActivitySpec {
            Name = "If",
            Properties = new() { ["condition"] = "[flag]" },
            Children = [Leaf()],
            Else = [Leaf("other")]
        };

        var result = XamlBuilder.RenderFragment(spec);

        Assert.True(result.Success, string.Join(";", result.Errors.Select(e => e.Message)));
        Assert.Contains("<If.Then>", result.Xaml);
        Assert.Contains("<Sequence DisplayName=\"Then\">", result.Xaml);
        Assert.Contains("<If.Else>", result.Xaml);
        Assert.Contains("<Sequence DisplayName=\"Else\">", result.Xaml);
    }

    [Fact]
    public void TryCatch_TryAndEachCatch_AreSequenceWrapped() {
        var spec = new ActivitySpec {
            Name = "TryCatch",
            Children = [Leaf()],
            Catches = [new CatchSpec { Exception = "System.Exception", Children = [Leaf("handled")] }]
        };

        var result = XamlBuilder.RenderFragment(spec);

        Assert.True(result.Success, string.Join(";", result.Errors.Select(e => e.Message)));
        Assert.Contains("<Sequence DisplayName=\"Try\">", result.Xaml);
        Assert.Contains("<Sequence DisplayName=\"Catch\">", result.Xaml);
    }

    [Fact]
    public void ForEach_BodyIsSequenceWrappedAndUsesTheUiPathWrap() {
        var spec = new ActivitySpec {
            Name = "ForEach",
            Properties = new() { ["values"] = "[rows]", ["typeArgument"] = "String" },
            Children = [Leaf()]
        };

        var result = XamlBuilder.RenderFragment(spec);

        Assert.True(result.Success, string.Join(";", result.Errors.Select(e => e.Message)));
        Assert.Contains("<ui:ForEach", result.Xaml);
        Assert.Contains("<Sequence DisplayName=\"Body\">", result.Xaml);
    }

    [Fact]
    public void Switch_EachCaseAndDefault_AreSequenceWrapped() {
        var spec = new ActivitySpec {
            Name = "Switch",
            Properties = new() { ["expression"] = "[status]", ["typeArgument"] = "Int32" },
            Cases = [new SwitchCaseSpec { Key = "1", Children = [Leaf("one")] }],
            Default = [Leaf("none")]
        };

        var result = XamlBuilder.RenderFragment(spec);

        Assert.True(result.Success, string.Join(";", result.Errors.Select(e => e.Message)));
        // The single-child case is wrapped too, with the literal on the Sequence.
        Assert.Contains("<Sequence x:Key=\"1\">", result.Xaml);
        // The default branch is wrapped even though it holds one activity.
        var defaultStart = result.Xaml!.IndexOf("<Switch.Default>", StringComparison.Ordinal);
        Assert.True(defaultStart >= 0);
        var afterDefault = result.Xaml[defaultStart..];
        Assert.Contains("<Sequence>", afterDefault);
    }

    [Fact]
    public void BodyAlreadyASingleSequence_IsNotDoubleWrapped() {
        var spec = new ActivitySpec {
            Name = "If",
            Properties = new() { ["condition"] = "[flag]" },
            Children = [new ActivitySpec {
                Name = "Sequence",
                Properties = new() { ["displayName"] = "Then" },
                Children = [Leaf(), Leaf("second")]
            }]
        };

        var result = XamlBuilder.RenderFragment(spec);

        Assert.True(result.Success, string.Join(";", result.Errors.Select(e => e.Message)));
        // Exactly one Sequence in the Then branch — the caller's own, reused.
        Assert.Equal(1, Count(result.Xaml!, "<Sequence"));
        Assert.Contains("<Sequence DisplayName=\"Then\">", result.Xaml);
    }

    [Fact]
    public void While_MultipleChildrenInOneSequenceChild_WrapsOnce() {
        var spec = new ActivitySpec {
            Name = "While",
            Properties = new() { ["condition"] = "[n < 3]" },
            Children = [new ActivitySpec { Name = "Sequence", Children = [Leaf(), Leaf("second")] }]
        };

        // Studio's While toolbox item emits ui:InterruptibleWhile with a single
        // .Body slot; the Rule 24 wrap is the one Sequence inside it.
        var result = XamlBuilder.RenderFragment(spec);

        Assert.True(result.Success, string.Join(";", result.Errors.Select(e => e.Message)));
        Assert.Contains("<ui:InterruptibleWhile.Body>", result.Xaml);
        Assert.Equal(1, Count(result.Xaml!, "<Sequence"));
    }

    private static int Count(string text, string needle) {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0) {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
