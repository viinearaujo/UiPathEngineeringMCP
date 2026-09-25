namespace UiPath.Engineering.Mcp.AgentEvals;

internal static class AgentEvalConfig {
    public const string ApiKeyEnv = "AGENT_EVAL_API_KEY";
    public const string ModelEnv = "AGENT_EVAL_MODEL";
    public const string BaseUrlEnv = "AGENT_EVAL_BASE_URL";
    public const string DefaultBaseUrl = "https://api.openai.com/v1";
    public const int MaxToolRounds = 12;

    public static bool IsEnabled =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ApiKeyEnv))
        && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ModelEnv));

    public static string ApiKey => Environment.GetEnvironmentVariable(ApiKeyEnv)!.Trim();

    public static string Model => Environment.GetEnvironmentVariable(ModelEnv)!.Trim();

    public static string BaseUrl {
        get {
            var raw = Environment.GetEnvironmentVariable(BaseUrlEnv);
            return string.IsNullOrWhiteSpace(raw) ? DefaultBaseUrl : raw.Trim().TrimEnd('/');
        }
    }

    public static void SkipIfDisabled() {
        if (!IsEnabled) {
            Assert.Skip(
                $"{ApiKeyEnv} and {ModelEnv} must be set to run AgentEval (optional {BaseUrlEnv}, default {DefaultBaseUrl}).");
        }
    }
}
