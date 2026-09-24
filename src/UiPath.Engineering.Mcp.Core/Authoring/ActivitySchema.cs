namespace UiPath.Engineering.Mcp.Core.Authoring;

public enum PropertyKind {
    Expression,
    Literal,
    TypeArgument
}

/// <summary>
/// The WF argument wrapper an expression property is bound through. Decides both
/// the element name (InArgument / OutArgument / InOutArgument) and, in a C#
/// project, whether the binding is a CSharpValue (read) or CSharpReference
/// (write) — an lvalue must be a reference.
/// </summary>
public enum ArgumentDirection {
    In,
    Out,
    InOut
}

/// <summary>
/// The shape of a container activity's activity body. This is the missing piece
/// of information that used to make the validator blind to the container-shape
/// defects: a schema said <c>IsContainer = true</c> but never said whether the
/// body is a direct child <c>Activity</c>, a property element holding a bare
/// <c>ActivityAction</c>, or an <c>ActivityAction&lt;T&gt;</c> with a
/// <c>DelegateInArgument</c>.
/// </summary>
public enum BodyShape {
    /// <summary>Not a container.</summary>
    None,
    /// <summary>The body is the activity's content property (a bare <c>Activity</c>), as on ForEach/While/DoWhile.</summary>
    Activity,
    /// <summary>A property element holding an untyped <c>ActivityAction</c>, as on RetryScope.ActivityBody.</summary>
    UntypedAction,
    /// <summary>A property element holding <c>ActivityAction&lt;T&gt;</c> + <c>DelegateInArgument</c>, as on ForEachRow.Body.</summary>
    TypedAction,
    /// <summary>Parameters arrive as an <c>&lt;Arguments&gt;</c> dictionary rather than as a body.</summary>
    ArgumentDictionary,
    /// <summary>The body is a <c>.Body</c> property element holding a bare <c>Sequence</c> (UiPath scope cards, e.g. NApplicationCard).</summary>
    ActivityCollection,
    /// <summary>Named branch slots on one activity (If.Then/.Else, Switch cases/.Default, TryCatch.Try/Catches/Finally).</summary>
    Branches
}

/// <summary>
/// How a container activity receives its body, plus the property element and
/// delegate details needed to render it correctly.
/// </summary>
public sealed record BodyDescriptor(
    BodyShape Shape,
    string Property,
    string? DelegateType = null,
    string? IteratorName = null);

/// <summary>
/// One property of an activity. <paramref name="ClrType"/>, <paramref name="Default"/>,
/// <paramref name="AllowedValues"/> and <paramref name="Direction"/> are the surface
/// information the validator needs to check a spec for real — without them a
/// discovered activity validated as <c>valid: true</c> for any property bag.
/// </summary>
public sealed record PropertySchema(
    string Name,
    bool Required,
    PropertyKind Kind,
    string? ClrType = null,
    string? Default = null,
    IReadOnlyList<string>? AllowedValues = null,
    ArgumentDirection? Direction = null,
    bool IsContentProperty = false);

public sealed record ActivitySchema(
    string Name, string Prefix, string XmlNamespace, bool IsContainer,
    IReadOnlyList<PropertySchema> Properties, bool Experimental = false,
    string? PackageId = null, string? PackageVersion = null, string? FullTypeName = null,
    BodyDescriptor? Body = null, string? ElementName = null) {
    /// <summary>
    /// The XML element local name to emit. Differs from <see cref="Name"/> only
    /// where Studio's toolbox label is not the emitted type: the "While",
    /// "Do While", and "For Each" toolbox items emit
    /// <c>InterruptibleWhile</c> / <c>InterruptibleDoWhile</c> / <c>ForEach</c>.
    /// Lookups accept both names.
    /// </summary>
    public string RenderName => ElementName ?? Name;
}