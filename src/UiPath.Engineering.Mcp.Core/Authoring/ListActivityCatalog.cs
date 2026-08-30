using System.Diagnostics.CodeAnalysis;

namespace UiPath.Engineering.Mcp.Core.Authoring;

public sealed class ListActivityCatalog : IActivityCatalog
{
    private readonly IReadOnlyDictionary<string, ActivitySchema> _byName;

    public ListActivityCatalog(IReadOnlyList<ActivitySchema> all, string source)
    {
        All = all;
        Source = source;
        var map = new Dictionary<string, ActivitySchema>(StringComparer.OrdinalIgnoreCase);
        foreach (var schema in all)
        {
            map.TryAdd(schema.Name, schema);
        }
        _byName = map;
    }

    public IReadOnlyList<ActivitySchema> All { get; }
    public string Source { get; }

    public bool TryGet(string name, [NotNullWhen(true)] out ActivitySchema? schema) =>
        _byName.TryGetValue(name, out schema);

    public string? Suggest(string name) => ActivityCatalog.Suggest(name, All);
}
