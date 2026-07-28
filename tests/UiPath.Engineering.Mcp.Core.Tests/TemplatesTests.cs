using System.Xml.Linq;
using UiPath.Engineering.Mcp.Core.Templates;

namespace UiPath.Engineering.Mcp.Core.Tests;

public class TemplatesTests
{
    [Fact]
    public void BlankWorkflow_ProducesWellFormedXmlWithXamlClass()
    {
        var xaml = XamlWorkflowTemplates.BlankWorkflow("Workflows_SendEmail");

        var doc = XDocument.Parse(xaml);
        Assert.Equal("Workflows_SendEmail", doc.Root!.Attribute(XName.Get("Class", "http://schemas.microsoft.com/winfx/2006/xaml"))!.Value);
        Assert.Contains("netfx/2009/xaml/activities", doc.Root.Name.NamespaceName);
    }

    [Theory]
    [InlineData("SendEmail.xaml", "SendEmail")]
    [InlineData("Workflows/SendEmail.xaml", "Workflows_SendEmail")]
    [InlineData("Workflows\\Nested\\SendEmail.xaml", "Workflows_Nested_SendEmail")]
    [InlineData("SendEmail", "SendEmail")]
    public void ToXamlClassName_MapsRelativePathToUnderscoredClass(string relative, string expected)
    {
        Assert.Equal(expected, XamlWorkflowTemplates.ToXamlClassName(relative));
    }

    [Fact]
    public void CodedWorkflow_ContainsBaseClassWorkflowAttributeAndNamespace()
    {
        var code = CodedWorkflowTemplates.CodedWorkflow("My_Project", "InvoiceFlow");

        Assert.Contains("class InvoiceFlow : CodedWorkflow", code);
        Assert.Contains("[Workflow]", code);
        Assert.Contains("namespace My_Project", code);
        Assert.Contains("using UiPath.CodedWorkflows;", code);
    }

    [Fact]
    public void CodedSourceFile_HasNoWorkflowAttributeOrBaseClass()
    {
        var code = CodedWorkflowTemplates.CodedSourceFile("My_Project", "Helpers");

        Assert.Contains("class Helpers", code);
        Assert.DoesNotContain("CodedWorkflow", code);
        Assert.DoesNotContain("[Workflow]", code);
    }

    [Theory]
    [InlineData("My Project", "MyProject")]
    [InlineData("my-project", "my_project")]
    [InlineData("123Project", "_123Project")]
    [InlineData("a.b c-d", "a_bc_d")]
    public void SanitizeNamespace_ProducesValidIdentifier(string projectName, string expected)
    {
        Assert.Equal(expected, CodedWorkflowTemplates.SanitizeNamespace(projectName));
    }

    [Theory]
    [InlineData("InvoiceFlow", true)]
    [InlineData("_Private", true)]
    [InlineData("9Lives", false)]
    [InlineData("Has Space", false)]
    [InlineData("", false)]
    public void IsValidClassName_ValidatesIdentifiers(string name, bool expected)
    {
        Assert.Equal(expected, CodedWorkflowTemplates.IsValidClassName(name));
    }
}
