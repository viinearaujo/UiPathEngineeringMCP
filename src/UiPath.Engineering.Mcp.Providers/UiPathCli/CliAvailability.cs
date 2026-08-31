namespace UiPath.Engineering.Mcp.Providers.UiPathCli;

/// <summary>
/// PATH probe for the configured UiPath CLI name. Used by readiness; does not run the CLI.
/// </summary>
public static class CliAvailability {
    public static bool IsPresent(string executablePath) =>
        CliExecutableResolver.Resolve(executablePath) is not null;
}
