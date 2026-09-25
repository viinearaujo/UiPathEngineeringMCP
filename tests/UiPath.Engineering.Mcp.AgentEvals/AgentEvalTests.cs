namespace UiPath.Engineering.Mcp.AgentEvals;

public sealed class AgentEvalTests {
    [Fact]
    [Trait("Category", "AgentEval")]
    public async Task AgentEval_ScriptedTasks_Scorecard() {
        AgentEvalConfig.SkipIfDisabled();

        var repoRoot = AgentTaskLoader.FindRepoRoot();
        var tasks = AgentTaskLoader.LoadAll(repoRoot);

        using var llm = new OpenAiCompatibleClient(AgentEvalConfig.BaseUrl, AgentEvalConfig.ApiKey);
        var runner = new AgentEvalRunner(repoRoot, llm);
        var results = await runner.RunAllAsync(tasks, TestContext.Current.CancellationToken);
        ScorecardWriter.Write(repoRoot, results, skipped: false);

        var failed = results.Where(r => !r.Passed).Select(r => $"{r.Id}: {r.Detail}").ToArray();
        Assert.True(
            failed.Length == 0,
            "AgentEval failures:" + Environment.NewLine + string.Join(Environment.NewLine, failed));
    }
}
