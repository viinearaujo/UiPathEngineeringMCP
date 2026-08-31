namespace UiPath.Engineering.Mcp.Core.Models;

/// <summary>
/// The three UiPath .cs identities. A coded workflow is an entry point;
/// a coded test case is registered in fileInfoCollection only; a coded source
/// file is a plain helper class that XAML must never call.
/// </summary>
public static class CodedFileKind {
    public const string Workflow = "workflow";
    public const string Test = "test";
    public const string Source = "source";
}
