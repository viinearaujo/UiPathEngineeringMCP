# Copilot Tool Selection Fix Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make Microsoft 365 Copilot reliably select the file-reading MCP tools (`read_workflow_file`, `list_skills`, `read_skill`) by fixing the two text surfaces that drive Copilot Studio tool selection.

**Architecture:** No server logic changes. Edit the Copilot Studio agent instructions file (explicit routing rules + stale-name fix) and prepend intent-matching trigger phrasing to the `read_workflow_file` tool description. Verify via `dotnet test`, MCP Inspector, and a scripted Copilot prompt battery.

**Tech Stack:** .NET 8, ModelContextProtocol C# SDK, Copilot Studio, MCP Inspector (`npx @modelcontextprotocol/inspector`).

**Spec:** `docs/superpowers/specs/2026-07-30-copilot-tool-selection-design.md`

## Global Constraints

- Description/attribute edits only — no executable code changes anywhere.
- No test asserts on `[Description]` attribute text (verified 2026-07-30); tests must pass unchanged.
- `run_uip_cli` is entirely out of scope, including the instructions update.
- No new tools, no new infrastructure, no new MCP servers.
- `ListSkillsTool.cs` / `ReadSkillTool.cs` descriptions are touched ONLY if the Task 3 Copilot battery shows they are still skipped (conditional step there).

---

### Task 1: Update Copilot Studio agent instructions

**Files:**
- Modify: `docs/copilot-studio-agent-instructions.txt`

**Interfaces:**
- Consumes: nothing.
- Produces: the agent instructions text the user will paste into the Copilot Studio agent configuration in Task 3. Tool names referenced must be exact MCP tool names: `read_workflow_file`, `list_skills`, `read_skill`, `add_coded_workflow`.

Current file content (31 lines) has three problems:
1. No routing rules telling Copilot *when* to call the reading tools.
2. Line 13 references `create_coded_workflow` — a tool that does not exist (real name: `add_coded_workflow`).
3. IN SCOPE list omits the reading tools.

- [ ] **Step 1: Fix the stale tool name**

In `docs/copilot-studio-agent-instructions.txt`, line 13, change:

```text
- Scaffold projects (create_project), add workflows (add_xaml_workflow, create_coded_workflow)
```

to:

```text
- Scaffold projects (create_project), add workflows (add_xaml_workflow, add_coded_workflow)
```

- [ ] **Step 2: Add the reading tools to IN SCOPE**

In the IN SCOPE section, after the line `- Documentation generation (generate_documentation) and GitLab work items (create_work_items, search_repository)`, add:

```text
- Read any project file's contents (read_workflow_file)
- UiPath skills playbooks (list_skills, read_skill)
```

- [ ] **Step 3: Add the FILE AND SKILL READING routing block**

Insert a new section immediately after the "TOOL USE RULES (MOST IMPORTANT)" block (i.e., before "IN SCOPE"):

```text
FILE AND SKILL READING
- When the user asks about the contents of any file in a project (XAML, .cs, JSON, configs, docs, "what does X say", "show me line N"), call read_workflow_file with the projectPath and relativePath. Use startLine/lineCount to page long files.
- Before starting any UiPath build or edit task, call list_skills, then read_skill for the relevant skill, and follow its playbook.
```

- [ ] **Step 4: Review the full file**

Run: `git diff docs/copilot-studio-agent-instructions.txt`
Expected: exactly the three edits above; no other lines changed; all tool names spelled exactly as the MCP tool names.

- [ ] **Step 5: Commit**

```bash
git add docs/copilot-studio-agent-instructions.txt
git commit -m "docs: add file/skill reading routing rules to Copilot agent instructions"
```

---

### Task 2: Sharpen the `read_workflow_file` tool description

**Files:**
- Modify: `src/UiPath.Engineering.Mcp.Tools/ReadWorkflowFileTool.cs:22`

**Interfaces:**
- Consumes: nothing.
- Produces: the tool description the MCP server advertises to Copilot Studio. Tool name and method signature (`ToolResult ReadWorkflowFile(string projectPath, string relativePath, int? startLine = null, int? lineCount = null)`) must not change.

- [ ] **Step 1: Edit the Description attribute (attribute text only)**

In `src/UiPath.Engineering.Mcp.Tools/ReadWorkflowFileTool.cs`, line 22, replace:

```csharp
    [McpServerTool, Description("Reads the text content of any file inside an existing UiPath project, with line numbers and pagination. Obvious secret values are redacted. Use startLine/lineCount to page through large files.")]
```

with:

```csharp
    [McpServerTool, Description("Reads the contents of any text file inside a UiPath project (XAML, .cs, JSON, configs, docs), with line numbers and pagination. Use this whenever the user asks what a file contains, to show specific lines, or to inspect project configuration. Obvious secret values are redacted; .env, *.pem and *.key files are refused. Use startLine/lineCount to page through large files.")]
```

Do not touch the method signature, parameter descriptions, or any logic.

- [ ] **Step 2: Build and run the full test suite**

Run: `dotnet build && dotnet test`
Expected: build succeeds; all tests pass with zero test modifications (this is the guard that no logic was touched).

- [ ] **Step 3: Verify the diff is attribute-only**

Run: `git diff src/UiPath.Engineering.Mcp.Tools/ReadWorkflowFileTool.cs`
Expected: exactly one line changed — the `Description(...)` string on line 22.

- [ ] **Step 4: Commit**

```bash
git add src/UiPath.Engineering.Mcp.Tools/ReadWorkflowFileTool.cs
git commit -m "feat: sharpen read_workflow_file description for Copilot tool selection"
```

---

### Task 3: End-to-end verification (server + Copilot battery)

**Files:**
- No files modified. Manual verification only.

**Interfaces:**
- Consumes: Task 1's updated instructions text (to paste into Copilot Studio) and Task 2's new server description.
- Produces: a pass/fail record per battery prompt; the go/no-go decision on the conditional ListSkills/ReadSkill description touch-ups.

- [ ] **Step 1: Start the server and confirm the advertised description**

```powershell
dotnet run --project src/UiPath.Engineering.Mcp.Server
npx @modelcontextprotocol/inspector
# Connect to: http://localhost:5000/sse (transport: Streamable HTTP)
```

Expected: `read_workflow_file` appears with the new description text ("Use this whenever the user asks what a file contains...").

- [ ] **Step 2: Prove server-side behavior in MCP Inspector**

Manually invoke `read_workflow_file` against the known test project (from the implementation plan's confirmed environment):

```json
{
  "projectPath": "C:/Users/arauj/OneDrive/Documentos/UiPath/testProcess",
  "relativePath": "project.json",
  "lineCount": 20
}
```

If that project is unavailable, substitute any project inside the configured `Projects:AllowedRoots` (see `src/UiPath.Engineering.Mcp.Server/appsettings.json`).

Expected: structured JSON with numbered lines. This isolates "server works" from "agent calls it".

- [ ] **Step 3: Update the Copilot Studio agent**

In Copilot Studio, replace the agent's instructions with the updated text from `docs/copilot-studio-agent-instructions.txt` (Task 1 output). Refresh the MCP tool list so the new `read_workflow_file` description is picked up.

- [ ] **Step 4: Run the Copilot prompt battery**

Send these prompts to the Copilot agent one at a time (X = the test project `C:/Users/arauj/OneDrive/Documentos/UiPath/testProcess`, or any project inside the configured allowed roots):

1. "Read the first 50 lines of Main.xaml in project X" → expected tool call: `read_workflow_file`
2. "What does the Config.json in project X contain?" → expected tool call: `read_workflow_file`
3. "Load the uipath-rpa skill" → expected tool calls: `list_skills` then `read_skill`

Record pass/fail per prompt (expected: 3/3 pass).

- [ ] **Step 5 (conditional): Touch up skill-tool descriptions only if prompt 3 failed**

If prompt 3 still does not trigger `list_skills`/`read_skill`, apply these attribute-only edits, then rerun `dotnet build && dotnet test` and prompt 3:

In `src/UiPath.Engineering.Mcp.Tools/ListSkillsTool.cs`, line 18, replace the description with:

```csharp
    [McpServerTool, Description("Lists the UiPath skills catalog (name + description) — the playbooks for UiPath tasks. Use this when the user mentions skills, playbooks, or before starting any UiPath build or edit task. Call read_skill with a name from this list to load the full instructions.")]
```

In `src/UiPath.Engineering.Mcp.Tools/ReadSkillTool.cs`, line 18, replace the description with:

```csharp
    [McpServerTool, Description("Reads the full content of a UiPath skill (its SKILL.md playbook, or an auxiliary file inside the skill directory via the file parameter). Use this when the user asks to load, read, or follow a skill or playbook. Use list_skills first to discover names.")]
```

If applied, commit:

```bash
git add src/UiPath.Engineering.Mcp.Tools/ListSkillsTool.cs src/UiPath.Engineering.Mcp.Tools/ReadSkillTool.cs
git commit -m "feat: sharpen skills tool descriptions for Copilot tool selection"
```

If prompt 3 passed, skip this step entirely and note that in the verification record.

- [ ] **Step 6: Record results**

Report the battery outcome (pass/fail per prompt, whether Step 5 was needed). If any prompt still fails after Step 5, stop and report — per the spec, that means the problem is Copilot Studio-side and the design's fallback (separate filesystem MCP server) needs reconsideration.
