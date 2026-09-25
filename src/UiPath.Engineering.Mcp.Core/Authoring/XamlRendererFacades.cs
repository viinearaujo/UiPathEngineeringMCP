using System.Xml.Linq;
using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Core.Authoring;

/// <summary>Container body-shape rendering (If/Switch/TryCatch and schema BodyShape forms).</summary>
public static class XamlContainerRenderer {
    public static XElement RenderByBodyShape(XamlRenderContext ctx, ActivitySpec spec, ActivitySchema schema)
        => ctx.RenderByBodyShape(spec, schema);

    public static XElement RenderIf(XamlRenderContext ctx, ActivitySpec spec, ActivitySchema schema)
        => ctx.RenderIf(spec, schema);

    public static XElement RenderSwitch(XamlRenderContext ctx, ActivitySpec spec, ActivitySchema schema)
        => ctx.RenderSwitch(spec, schema);

    public static XElement RenderTryCatch(XamlRenderContext ctx, ActivitySpec spec, ActivitySchema schema)
        => ctx.RenderTryCatch(spec, schema);
}

/// <summary>Flowchart / StateMachine graph rendering with x:Reference wiring and shape ViewState.</summary>
public static class XamlDiagramRenderer {
    public static XElement RenderFlowchart(XamlRenderContext ctx, ActivitySpec spec, ActivitySchema schema)
        => ctx.RenderFlowchart(spec, schema);

    public static XElement RenderStateMachine(XamlRenderContext ctx, ActivitySpec spec, ActivitySchema schema)
        => ctx.RenderStateMachine(spec, schema);
}

/// <summary>Workflow members, language blocks, Assign, Arguments dictionaries, and variables.</summary>
public static class XamlMembersRenderer {
    public static XElement? Members(XamlRenderContext ctx, ActivitySpec spec) => ctx.Members(spec);

    public static XElement? LanguageBlock(XamlRenderContext ctx, ActivitySpec spec) => ctx.LanguageBlock(spec);

    public static XElement RenderAssign(XamlRenderContext ctx, ActivitySpec spec, ActivitySchema schema)
        => ctx.RenderAssign(spec, schema);

    public static XElement RenderArguments(XamlRenderContext ctx, ActivitySpec spec, ActivitySchema schema)
        => ctx.RenderArguments(spec, schema);

    public static XElement RenderVariables(XamlRenderContext ctx, List<VariableSpec> variables)
        => ctx.RenderVariables(variables);
}

/// <summary>Activity attributes and C#/VB expression property-element rendering.</summary>
public static class XamlAttributeRenderer {
    public static List<XAttribute> Attributes(XamlRenderContext ctx, ActivitySpec spec, ActivitySchema schema, string? exclude = null)
        => ctx.Attributes(spec, schema, exclude);

    public static void AddExpressionProperties(XamlRenderContext ctx, XElement element, ActivitySpec spec, ActivitySchema schema)
        => ctx.AddExpressionProperties(element, spec, schema);
}
