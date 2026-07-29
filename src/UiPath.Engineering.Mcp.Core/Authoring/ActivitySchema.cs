namespace UiPath.Engineering.Mcp.Core.Authoring;

public enum PropertyKind
{
    Expression,
    Literal,
    TypeArgument
}

public sealed record PropertySchema(string Name, bool Required, PropertyKind Kind);

public sealed record ActivitySchema(
    string Name, string Prefix, string XmlNamespace, bool IsContainer,
    IReadOnlyList<PropertySchema> Properties, bool Experimental = false);
