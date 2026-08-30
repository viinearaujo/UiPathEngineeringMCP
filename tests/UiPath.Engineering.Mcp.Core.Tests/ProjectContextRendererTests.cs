using UiPath.Engineering.Mcp.Core.Docs;
using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Core.Tests;

public class ProjectContextRendererTests {
    [Fact]
    public void RenderMarkdown_IncludesMetadataAndCaps() {
        var model = new UiPathProjectModel {
            ProjectName = "demo",
            ProjectPath = "/p",
            ExpressionLanguage = "CSharp",
            OutputType = "Process",
            MainWorkflow = "Main.xaml",
            Workflows = [new WorkflowModel { FileName = "Main.xaml", IsMain = true }],
            CodedWorkflows = [new CodedWorkflowModel { FileName = "Worker.cs" }],
            Packages = [new PackageModel { Id = "UiPath.System.Activities", Version = "24.10.4" }]
        };

        var markdown = new ProjectContextRenderer(new FakeFilesystemProvider()).RenderMarkdown(model);

        Assert.Contains("discovery-metadata: cs=1 xaml=1 deps=1", markdown);
        Assert.Contains("Expression language: CSharp", markdown);
        Assert.Contains("Output type: Process", markdown);
        Assert.True(ProjectContextRenderer.TryParseMetadata(markdown, out var cs, out var xaml, out var deps));
        Assert.Equal(1, cs);
        Assert.Equal(1, xaml);
        Assert.Equal(1, deps);
    }

    [Fact]
    public void SpliceAgentsMarkdown_ReplacesExistingBlock() {
        var renderer = new ProjectContextRenderer(new FakeFilesystemProvider());
        var existing = "Intro\n" + renderer.WrapBlock("old") + "Outro\n";

        var spliced = renderer.SpliceAgentsMarkdown(existing, "new body");

        Assert.Contains("new body", spliced);
        Assert.DoesNotContain("old", spliced);
        Assert.Contains("Intro", spliced);
        Assert.Contains("Outro", spliced);
        Assert.True(ProjectContextRenderer.HasMarkers(spliced));
    }

    [Fact]
    public void Sync_WritesAgentsAndProjectContext() {
        var fs = new FakeFilesystemProvider();
        var project = Path.Combine(Path.GetTempPath(), "mcp-ctx-" + Guid.NewGuid().ToString("N"));
        var renderer = new ProjectContextRenderer(fs);
        var model = new UiPathProjectModel { ProjectName = "demo", ProjectPath = project };

        renderer.Sync(project, model);

        Assert.True(fs.FileExists(ProjectDocsPaths.AgentsMd(project)));
        Assert.True(fs.FileExists(ProjectDocsPaths.ProjectContext(project)));
        Assert.Contains(ProjectContextRenderer.StartMarker, fs.ReadAllText(ProjectDocsPaths.AgentsMd(project)));
    }
}
