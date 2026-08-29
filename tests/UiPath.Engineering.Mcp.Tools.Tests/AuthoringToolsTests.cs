using System.Text.Json;
using UiPath.Engineering.Mcp.Providers.UiPathCli;

namespace UiPath.Engineering.Mcp.Tools.Tests;

public class AddXamlWorkflowToolTests {
    private const string ProjectPath = "/projects/testProcess";

    [Fact]
    public void AddXamlWorkflow_WhenPathNotAllowed_ReturnsError() {
        var fs = new FakeFilesystemProvider { Allowed = false };
        var tool = new AddXamlWorkflowTool(fs);

        var result = tool.AddXamlWorkflow(ProjectPath, "SendEmail.xaml");

        Assert.Equal("error", result.Status);
    }

    [Fact]
    public void AddXamlWorkflow_WhenProjectJsonMissing_ReturnsError() {
        var fs = new FakeFilesystemProvider { ProjectJson = null };
        var tool = new AddXamlWorkflowTool(fs);

        var result = tool.AddXamlWorkflow(ProjectPath, "SendEmail.xaml");

        Assert.Equal("error", result.Status);
        Assert.Contains("project.json", result.Summary);
    }

    [Fact]
    public void AddXamlWorkflow_WhenFileExists_ReturnsError() {
        var fs = new FakeFilesystemProvider();
        var tool = new AddXamlWorkflowTool(fs);
        var existing = Path.Combine(Path.GetFullPath(ProjectPath), "SendEmail.xaml");
        fs.ExistingFiles.Add(existing);

        var result = tool.AddXamlWorkflow(ProjectPath, "SendEmail.xaml");

        Assert.Equal("error", result.Status);
        Assert.Contains("already exists", result.Summary);
    }

    [Fact]
    public void AddXamlWorkflow_WhenPathEscapesProject_ReturnsError() {
        var fs = new FakeFilesystemProvider();
        var tool = new AddXamlWorkflowTool(fs);

        var result = tool.AddXamlWorkflow(ProjectPath, "../Outside.xaml");

        Assert.Equal("error", result.Status);
        Assert.Empty(fs.Writes);
    }

    [Fact]
    public void AddXamlWorkflow_HappyPath_WritesTemplateWithUnderscoredClass() {
        var fs = new FakeFilesystemProvider();
        var tool = new AddXamlWorkflowTool(fs);

        var result = tool.AddXamlWorkflow(ProjectPath, "Workflows/SendEmail.xaml");

        Assert.Equal("success", result.Status);
        var target = Path.Combine(Path.GetFullPath(ProjectPath), "Workflows", "SendEmail.xaml");
        Assert.True(fs.Writes.ContainsKey(target));
        Assert.Contains("x:Class=\"Workflows_SendEmail\"", fs.Writes[target]);
        Assert.Contains("<x:Members>", fs.Writes[target]);
    }
}

public class WriteWorkflowFileToolTests {
    private const string ProjectPath = "/projects/testProcess";

    [Fact]
    public void WriteWorkflowFile_RejectsDisallowedExtension() {
        var fs = new FakeFilesystemProvider();
        var tool = new WriteWorkflowFileTool(fs);

        var result = tool.WriteWorkflowFile(ProjectPath, "notes.txt", "hello");

        Assert.Equal("error", result.Status);
        Assert.Empty(fs.Writes);
    }

    [Fact]
    public void WriteWorkflowFile_RejectsPathOutsideProject() {
        var fs = new FakeFilesystemProvider();
        var tool = new WriteWorkflowFileTool(fs);

        var result = tool.WriteWorkflowFile(ProjectPath, "../../evil.xaml", "<x/>");

        Assert.Equal("error", result.Status);
        Assert.Empty(fs.Writes);
    }

    [Fact]
    public void WriteWorkflowFile_CreatesNewFile() {
        var fs = new FakeFilesystemProvider();
        var tool = new WriteWorkflowFileTool(fs);

        var result = tool.WriteWorkflowFile(ProjectPath, "Main.xaml", "<Activity />");
        var data = JsonSerializer.SerializeToElement(result.Data);

        Assert.Equal("success", result.Status);
        Assert.False(data.GetProperty("overwritten").GetBoolean());
        Assert.Equal("<Activity />", fs.Writes[Path.Combine(Path.GetFullPath(ProjectPath), "Main.xaml")]);
    }

    [Fact]
    public void WriteWorkflowFile_OverwritesExistingFile() {
        var fs = new FakeFilesystemProvider();
        var target = Path.Combine(Path.GetFullPath(ProjectPath), "Main.xaml");
        fs.ExistingFiles.Add(target);
        var tool = new WriteWorkflowFileTool(fs);

        var result = tool.WriteWorkflowFile(ProjectPath, "Main.xaml", "<Activity />");
        var data = JsonSerializer.SerializeToElement(result.Data);

        Assert.Equal("success", result.Status);
        Assert.True(data.GetProperty("overwritten").GetBoolean());
    }

    [Fact]
    public void WriteWorkflowFile_Success_IncludesSha256AndXamlClass() {
        var fs = new FakeFilesystemProvider();
        var tool = new WriteWorkflowFileTool(fs);
        var content = """
            <Activity x:Class="Main" xmlns="http://schemas.microsoft.com/netfx/2009/xaml/activities"
                      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <Sequence />
            </Activity>
            """;

        var result = tool.WriteWorkflowFile(ProjectPath, "Main.xaml", content);
        var data = JsonSerializer.SerializeToElement(result.Data);

        Assert.Equal("success", result.Status);
        Assert.Equal(content.Length, data.GetProperty("bytesWritten").GetInt32());
        var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content)));
        Assert.Equal(expected, data.GetProperty("sha256").GetString());
        Assert.Equal("Main", data.GetProperty("className").GetString());
    }

    [Fact]
    public void WriteWorkflowFile_Cs_IncludesClassName() {
        var fs = new FakeFilesystemProvider();
        var tool = new WriteWorkflowFileTool(fs);
        var content = "namespace N;\npublic class InvoiceFlow : CodedWorkflow { }";

        var result = tool.WriteWorkflowFile(ProjectPath, "InvoiceFlow.cs", content);
        var data = JsonSerializer.SerializeToElement(result.Data);

        Assert.Equal("InvoiceFlow", data.GetProperty("className").GetString());
    }
}

public class CreateCodedWorkflowToolTests {
    private const string ProjectPath = "/projects/testProcess";

    private static FakeFilesystemProvider CreateFs() => new() {
        ProjectJsonContent = """{ "name": "My Test-Project", "entryPoints": [] }"""
    };

    [Fact]
    public void AddCodedWorkflow_RejectsInvalidClassName() {
        var tool = new CreateCodedWorkflowTool(CreateFs());

        var result = tool.AddCodedWorkflow(ProjectPath, "9Invalid");

        Assert.Equal("error", result.Status);
    }

    [Fact]
    public void AddCodedWorkflow_RejectsInvalidKind() {
        var tool = new CreateCodedWorkflowTool(CreateFs());

        var result = tool.AddCodedWorkflow(ProjectPath, "Flow", "bogus");

        Assert.Equal("error", result.Status);
    }

    [Fact]
    public void AddCodedWorkflow_Workflow_WritesFileAndRegistersEntryPoint() {
        var fs = CreateFs();
        var tool = new CreateCodedWorkflowTool(fs);
        var projectJsonPath = fs.ProjectJson!;

        var result = tool.AddCodedWorkflow(ProjectPath, "InvoiceFlow");
        var data = JsonSerializer.SerializeToElement(result.Data);

        Assert.Equal("success", result.Status);
        Assert.True(data.GetProperty("entryPointRegistered").GetBoolean());
        Assert.Equal("MyTest_Project", data.GetProperty("namespace").GetString());

        var csPath = Path.Combine(Path.GetFullPath(ProjectPath), "InvoiceFlow.cs");
        Assert.Contains("class InvoiceFlow : CodedWorkflow", fs.Writes[csPath]);

        using var updatedJson = JsonDocument.Parse(fs.Writes[projectJsonPath]);
        var entry = Assert.Single(updatedJson.RootElement.GetProperty("entryPoints").EnumerateArray());
        Assert.Equal("InvoiceFlow.cs", entry.GetProperty("filePath").GetString());
        Assert.True(Guid.TryParse(entry.GetProperty("uniqueId").GetString(), out _));
    }

    [Fact]
    public void AddCodedWorkflow_Source_DoesNotTouchProjectJson() {
        var fs = CreateFs();
        var tool = new CreateCodedWorkflowTool(fs);

        var result = tool.AddCodedWorkflow(ProjectPath, "Helpers", "source");
        var data = JsonSerializer.SerializeToElement(result.Data);

        Assert.Equal("success", result.Status);
        Assert.False(data.GetProperty("entryPointRegistered").GetBoolean());
        Assert.False(fs.Writes.ContainsKey(fs.ProjectJson!));
        var csPath = Path.Combine(Path.GetFullPath(ProjectPath), "Helpers.cs");
        Assert.DoesNotContain("CodedWorkflow", fs.Writes[csPath]);
    }
}

public class CreateProjectToolTests {
    [Fact]
    public async Task CreateProject_WhenParentNotAllowed_ReturnsError() {
        var fs = new FakeFilesystemProvider { Allowed = false };
        var tool = new CreateProjectTool(new FakeUiPathCliProvider(), fs);

        var result = await tool.CreateProject("NewProject", "/not/allowed");

        Assert.Equal("error", result.Status);
    }

    [Fact]
    public async Task CreateProject_PassesExpectedArgumentsToCli() {
        var fs = new FakeFilesystemProvider();
        var cli = new FakeUiPathCliProvider();
        var tool = new CreateProjectTool(cli, fs);

        var result = await tool.CreateProject("NewProject", "/projects/uipath", "VisualBasic", "Portable", "desc");

        Assert.Equal("success", result.Status);
        Assert.Equal("rpa", cli.LastVerb);
        Assert.Contains("init", cli.LastArguments);
        Assert.Contains("--name \"NewProject\"", cli.LastArguments);
        Assert.Contains("--expression-language VisualBasic", cli.LastArguments);
        Assert.Contains("--target-framework Portable", cli.LastArguments);
    }

    [Fact]
    public async Task CreateProject_CliFailsAndNoProjectCreated_ReturnsError() {
        var fs = new FakeFilesystemProvider { ProjectJson = null };
        var cli = new FakeUiPathCliProvider {
            RunResult = new UiPathCliResult { Success = false, Errors = ["unknown command: rpa"] }
        };
        var tool = new CreateProjectTool(cli, fs);

        var result = await tool.CreateProject("NewProject", "/projects/uipath");

        Assert.Equal("error", result.Status);
        Assert.Contains("unknown command: rpa", result.Errors);
    }

    [Fact]
    public async Task CreateProject_CliFailsButProjectJsonExists_ReportsPartialSuccess() {
        var fs = new FakeFilesystemProvider(); // ProjectJson set -> artifact exists
        var cli = new FakeUiPathCliProvider {
            RunResult = new UiPathCliResult { Success = false, Errors = ["some warning-level failure"] }
        };
        var tool = new CreateProjectTool(cli, fs);

        var result = await tool.CreateProject("NewProject", "/projects/uipath");
        var data = JsonSerializer.SerializeToElement(result.Data);

        Assert.Equal("success", result.Status);
        Assert.True(data.GetProperty("partialSuccess").GetBoolean());
    }
}
