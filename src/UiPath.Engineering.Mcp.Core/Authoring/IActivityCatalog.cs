using System.Diagnostics.CodeAnalysis;

namespace UiPath.Engineering.Mcp.Core.Authoring;

public interface IActivityCatalog
{
    IReadOnlyList<ActivitySchema> All { get; }
    string Source { get; }
    bool TryGet(string name, [NotNullWhen(true)] out ActivitySchema? schema);
    string? Suggest(string name);
}
