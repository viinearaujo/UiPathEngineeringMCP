namespace UiPath.Engineering.Mcp.Core.Authoring;

public sealed class ActivitySpec
{
    public string Name { get; set; } = string.Empty;
    public Dictionary<string, string>? Properties { get; set; }
    public List<ActivitySpec>? Children { get; set; }
    public List<VariableSpec>? Variables { get; set; }   // allowed on the root spec only
    public List<CatchSpec>? Catches { get; set; }        // TryCatch only
}

public sealed class VariableSpec
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public string? Default { get; set; }
}

public sealed class CatchSpec
{
    public string Exception { get; set; } = "System.Exception";
    public List<ActivitySpec>? Children { get; set; }
}
