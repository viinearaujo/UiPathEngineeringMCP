using UiPath.Engineering.Mcp.Providers.UiPathCli;

namespace UiPath.Engineering.Mcp.Providers.Tests;

public class AnalyzerRulesParserTests {
    private const string WorkflowScopePayload = """
        Found 4 enabled rule(s):

        # Workflow
        [warning] ST-AMG-001 (Workflow) - Identify activities with post-migration action required annotations
          recommendation: Review and resolve the annotations, then remove them.
          docs: https://docs.uipath.com/en_US/studio/standalone/latest/user-guide/st-amg-001
        [error] ST-SEC-008 (Workflow) - SecureString Variable Usage
          recommendation: Use the Type Secure Text activity to log in.
          parameters: VariableDepthUsage=1
          docs: https://docs.uipath.com/en_US/studio/standalone/latest/user-guide/st-sec-008
        [warning] ST-NMG-002 (Workflow) - Arguments Naming Convention
          recommendation: Make sure all the arguments follow the naming convention.
          parameters: InRegex=^in_([A-Z]|[a-z])+([0-9])*$, OutRegex=^out_([A-Z]|[a-z])*$
          docs: https://docs.uipath.com/en_US/studio/standalone/latest/user-guide/st-nmg-002
        [info] ST-ANA-003 (Workflow) - Project Workflow Count
          docs: https://docs.uipath.com/en_US/studio/standalone/latest/user-guide/st-ana-003
        """;

    [Fact]
    public void Parse_ReadsIdSeverityScopeAndTitle() {
        var rules = AnalyzerRulesParser.Parse(WorkflowScopePayload);

        Assert.Equal(4, rules.Count);
        var first = rules[0];
        Assert.Equal("ST-AMG-001", first.Id);
        Assert.Equal("warning", first.Severity);
        Assert.Equal("Workflow", first.Scope);
        Assert.Equal("Identify activities with post-migration action required annotations", first.Title);
        Assert.Equal("studio-builtin", first.Source);
    }

    [Fact]
    public void Parse_ReadsRecommendationAndDocs() {
        var rules = AnalyzerRulesParser.Parse(WorkflowScopePayload);

        Assert.Equal("Use the Type Secure Text activity to log in.", rules[1].Recommendation);
        Assert.Equal(
            "https://docs.uipath.com/en_US/studio/standalone/latest/user-guide/st-sec-008",
            rules[1].Docs);
    }

    [Fact]
    public void Parse_RuleWithoutRecommendation_LeavesItNull() {
        var rules = AnalyzerRulesParser.Parse(WorkflowScopePayload);

        var info = Assert.Single(rules, r => r.Id == "ST-ANA-003");
        Assert.Equal("info", info.Severity);
        Assert.Null(info.Recommendation);
        Assert.NotNull(info.Docs);
    }

    [Fact]
    public void Parse_ParametersWithCommasInsideRegex_AreNotSplit() {
        var rules = AnalyzerRulesParser.Parse(WorkflowScopePayload);

        var naming = Assert.Single(rules, r => r.Id == "ST-NMG-002");
        Assert.Equal(2, naming.Parameters.Count);
        Assert.Equal("^in_([A-Z]|[a-z])+([0-9])*$", naming.Parameters["InRegex"]);
        Assert.Equal("^out_([A-Z]|[a-z])*$", naming.Parameters["OutRegex"]);
    }

    [Fact]
    public void Parse_SimpleParameter_ReadsValue() {
        var rules = AnalyzerRulesParser.Parse(WorkflowScopePayload);

        var secure = Assert.Single(rules, r => r.Id == "ST-SEC-008");
        Assert.Equal("1", secure.Parameters["VariableDepthUsage"]);
    }

    [Fact]
    public void Parse_PackageShippedRule_IsFlaggedAsPackageShipped() {
        var rules = AnalyzerRulesParser.Parse("""
            # Activity
            [error] MA-DBP-001 (Activity) - Package rule title
              recommendation: Fix it.
            """);

        var rule = Assert.Single(rules);
        Assert.Equal("package-shipped", rule.Source);
        Assert.Equal("Activity", rule.Scope);
    }

    [Fact]
    public void Parse_OtherOwnedRule_IsFlaggedAsPackage() {
        var rules = AnalyzerRulesParser.Parse("[error] UI-DBP-006 (Workflow) - Container Usage");

        Assert.Equal("package", Assert.Single(rules).Source);
    }

    [Fact]
    public void Parse_MultipleScopeHeadings_ScopeIsReadFromTheRuleLine() {
        var rules = AnalyzerRulesParser.Parse("""
            # Activity
            [warning] ST-NMG-004 (Activity) - Display Name Duplication

            # Project
            [error] ST-DBP-002 (Project) - High Arguments Count
            """);

        Assert.Equal(2, rules.Count);
        Assert.Equal("Activity", rules[0].Scope);
        Assert.Equal("Project", rules[1].Scope);
    }

    [Fact]
    public void Parse_EmptyScope_FallsBackToTheHeading() {
        var rules = AnalyzerRulesParser.Parse("""
            # Coded Workflow
            [error] ST-CWP-001 () - Coded rule title
            """);

        Assert.Equal("Coded Workflow", Assert.Single(rules).Scope);
    }

    [Fact]
    public void Parse_DuplicateRuleAcrossHeadings_KeepsBothEntries() {
        // The live CLI repeats rules that apply to more than one package scope; the tool reports
        // what the CLI reported rather than silently de-duplicating.
        var rules = AnalyzerRulesParser.Parse("""
            [warning] SY-USG-014 (Workflow) - Incorrect Execution Template Placeholders
            [warning] SY-USG-014 (Workflow) - Incorrect Execution Template Placeholders
            """);

        Assert.Equal(2, rules.Count);
    }

    [Fact]
    public void Parse_NoRulesMessage_ReturnsEmpty() {
        Assert.Empty(AnalyzerRulesParser.Parse("No analyzer rules found."));
        Assert.Empty(AnalyzerRulesParser.Parse(""));
        Assert.Empty(AnalyzerRulesParser.Parse(null));
    }

    [Fact]
    public void Parse_VerboseSeverity_IsNormalizedToInfo() {
        var rules = AnalyzerRulesParser.Parse("[verbose] ST-ANA-001 (Project) - Verbose rule");

        Assert.Equal("info", Assert.Single(rules).Severity);
    }

    [Fact]
    public void Parse_RecommendationContainingAColonAndUrl_IsNotTruncated() {
        var rules = AnalyzerRulesParser.Parse("""
            [error] ST-SEC-009 (Workflow) - SecureString Misusage
              recommendation: Expect no conversion from SecureString to String. See: https://example/guide
            """);

        Assert.Equal(
            "Expect no conversion from SecureString to String. See: https://example/guide",
            Assert.Single(rules).Recommendation);
    }

    [Fact]
    public void ParseData_ReadsTheMessageProperty() {
        using var document = System.Text.Json.JsonDocument.Parse(
            """{"message":"Found 1 enabled rule(s):\n\n# Workflow\n[error] ST-SEC-008 (Workflow) - Title\n  docs: https://example/st-sec-008"}""");

        var rules = AnalyzerRulesParser.ParseData(document.RootElement);

        var rule = Assert.Single(rules);
        Assert.Equal("ST-SEC-008", rule.Id);
        Assert.Equal("https://example/st-sec-008", rule.Docs);
    }

    [Fact]
    public void ParseData_NonObjectData_ReturnsEmpty() {
        using var document = System.Text.Json.JsonDocument.Parse("[]");

        Assert.Empty(AnalyzerRulesParser.ParseData(document.RootElement));
        Assert.Empty(AnalyzerRulesParser.ParseData(null));
    }
}
