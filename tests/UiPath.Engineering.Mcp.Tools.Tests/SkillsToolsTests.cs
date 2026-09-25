using UiPath.Engineering.Mcp.Providers.Skills;
using UiPath.Engineering.Mcp.Tools;

namespace UiPath.Engineering.Mcp.Tools.Tests;

public class SkillsToolsTests {
    [Fact]
    public async Task ListSkills_ReturnsCatalog() {
        var skills = new FakeSkillsProvider {
            Skills = [new SkillSummary("uipath-rpa", "RPA skill", "uipath-rpa")]
        };
        var sut = new ListSkillsTool(skills);

        var result = await sut.ListSkills();

        Assert.Equal("success", result.Status);
        Assert.Contains("1 skill", result.Summary);
    }

    [Fact]
    public async Task ListSkills_MissingRoot_ReturnsStructuredError() {
        var skills = new FakeSkillsProvider { ThrowRootMissing = true };
        var sut = new ListSkillsTool(skills);

        var result = await sut.ListSkills();

        Assert.Equal("error", result.Status);
        Assert.Contains(result.ErrorDetails, e => e.ErrorCode == "SKILLS_ROOT_MISSING");
    }

    [Fact]
    public async Task SearchKnowledge_Skill_Success_ReturnsRedactedContent() {
        var skills = new FakeSkillsProvider {
            ReadResult = new SkillReadResult {
                Success = true, SkillName = "uipath-rpa", File = "SKILL.md",
                Content = "playbook with password=hunter2 inside"
            }
        };
        var sut = new SearchKnowledgeTool(
            new FakeFilesystemProvider(),
            Microsoft.Extensions.Options.Options.Create(new UiPath.Engineering.Mcp.Core.Configuration.SkillsOptions()),
            skills);

        var result = await sut.SearchKnowledge(SearchKnowledgeTool.ModeSkill, "uipath-rpa");

        Assert.Equal("success", result.Status);
        Assert.Equal("uipath-rpa", skills.LastName);
        var data = result.Data!.ToString()!;
        Assert.DoesNotContain("hunter2", data);
    }

    [Fact]
    public async Task SearchKnowledge_Skill_UnknownSkill_SuggestsListSkills() {
        var skills = new FakeSkillsProvider {
            ReadResult = new SkillReadResult {
                ErrorCode = "SKILL_NOT_FOUND", ErrorMessage = "Skill 'nope' was not found.",
                AvailableSkills = ["uipath-rpa"]
            }
        };
        var sut = new SearchKnowledgeTool(
            new FakeFilesystemProvider(),
            Microsoft.Extensions.Options.Options.Create(new UiPath.Engineering.Mcp.Core.Configuration.SkillsOptions()),
            skills);

        var result = await sut.SearchKnowledge(SearchKnowledgeTool.ModeSkill, "nope");

        Assert.Equal("error", result.Status);
        var error = Assert.Single(result.ErrorDetails);
        Assert.Equal("SKILL_NOT_FOUND", error.ErrorCode);
        Assert.Equal("list_skills", error.SuggestedTool);
    }

    [Fact]
    public async Task SearchKnowledge_Skill_PathRejected_ReturnsStructuredError() {
        var skills = new FakeSkillsProvider {
            ReadResult = new SkillReadResult {
                ErrorCode = "SKILL_PATH_REJECTED", ErrorMessage = "'../x' escapes the skill directory."
            }
        };
        var sut = new SearchKnowledgeTool(
            new FakeFilesystemProvider(),
            Microsoft.Extensions.Options.Options.Create(new UiPath.Engineering.Mcp.Core.Configuration.SkillsOptions()),
            skills);

        var result = await sut.SearchKnowledge(SearchKnowledgeTool.ModeSkill, "uipath-rpa", file: "../x");

        Assert.Equal("error", result.Status);
        Assert.Contains(result.ErrorDetails, e => e.ErrorCode == "SKILL_PATH_REJECTED");
    }
}
