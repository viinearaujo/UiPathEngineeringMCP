# Copilot Tool Selection for File-Reading Tools — Design

Date: 2026-07-30
Status: Approved design, pending implementation plan

## 1. Problem

The UiPath Engineering MCP server exposes file-reading tools (`read_workflow_file`,
`list_skills`, `read_skill`) to Microsoft 365 Copilot via Dev Tunnel. The tools are
registered in Copilot Studio and appear in the agent's tool list, but the Copilot
orchestrator **never selects them** — it answers without calling them.

Root-cause model: tool selection in Copilot Studio is driven by two text surfaces,
and both are weak for these tools:

1. **Agent instructions** (`docs/copilot-studio-agent-instructions.txt`) — stale.
   It never mentions `read_workflow_file`, `list_skills`, `read_skill`, or
   `run_uip_cli`, and it references a tool name that does not exist
   (`create_coded_workflow`; the real tool is `add_coded_workflow`).
2. **Tool `[Description]` attributes** — they describe *what* the tools do, but not
   *when to pick them*, giving the orchestrator weak intent-matching signals.

An alternative approach (running the official filesystem MCP server instead) was
considered and rejected: it adds a second process, a second tunnel, and a Node
dependency, while not addressing the actual failure — tool selection, which is
server-independent.

## 2. Scope

In scope:

- `read_workflow_file`, `list_skills`, `read_skill` — make Copilot reliably select them.
- The agent instructions file and the three tools' description attributes.

Out of scope:

- No server logic changes, no new tools, no new infrastructure, no new MCP servers.
- `run_uip_cli` (left out unless testing shows the same selection problem).

## 3. Architecture

Nothing new is built. Two text artifacts change, then the behavior is re-tested:

```text
Before: Copilot prompt -> orchestrator -> (no matching intent) -> answers without tools
After:  Copilot prompt -> orchestrator -> routing rule in agent instructions
                                       -> sharpened tool description
                                       -> calls read_workflow_file / list_skills / read_skill
```

Changed files (exactly four):

- `docs/copilot-studio-agent-instructions.txt`
- `src/UiPath.Engineering.Mcp.Tools/ReadWorkflowFileTool.cs`
- `src/UiPath.Engineering.Mcp.Tools/ListSkillsTool.cs`
- `src/UiPath.Engineering.Mcp.Tools/ReadSkillTool.cs`

## 4. Component Changes

### 4.1 Agent instructions (`docs/copilot-studio-agent-instructions.txt`)

- Add a "FILE AND SKILL READING" rule block with explicit routing:
  - When the user asks about file contents, line numbers, configs, or "what does X
    say" -> call `read_workflow_file`.
  - Before starting UiPath work -> call `list_skills`, then `read_skill` for the
    relevant skill.
- Fix the stale `create_coded_workflow` reference -> `add_coded_workflow`.
- Add `read_workflow_file`, `list_skills`, and `read_skill` to the
  IN SCOPE list so the orchestrator treats them as fair game. (`run_uip_cli` stays
  out of this design's scope entirely, including the instructions update.)

### 4.2 Tool descriptions

- `ReadWorkflowFileTool.cs` — prepend intent-matching trigger phrasing, e.g.
  "Use this whenever you need to see the contents of any text file in a project
  (XAML, .cs, JSON, config, docs)..." The existing behavior description stays.
- `ListSkillsTool.cs` / `ReadSkillTool.cs` — already reasonably phrased; apply
  light touch-ups only if Copilot testing shows they are still skipped after the
  agent-instructions fix.

Description edits only — no executable code changes.

## 5. Error Handling

No new error handling: no logic changes, so the tools' existing structured-error
behavior is untouched. The one designed-for failure mode: if Copilot still does not
call the tools after both text surfaces are fixed, the problem is Copilot
Studio-side (schema quirk or orchestrator limits) and the fallback is to reconsider
a separate filesystem MCP server, now with real evidence.

## 6. Testing / Verification

1. `dotnet build` + `dotnet test` — must pass unchanged. This guards against
   accidental logic edits (the change set is description-only).
2. MCP Inspector against `http://localhost:5000/sse`:
   - Confirm the server advertises the new descriptions.
   - Manually invoke `read_workflow_file` against a real project to prove
     server-side behavior (isolates server vs. agent).
3. Copilot Studio prompt battery — fixed prompts, each with an expected tool call:
   - "Read the first 50 lines of Main.xaml in project X" -> `read_workflow_file`
   - "What does the Config.json in project X contain?" -> `read_workflow_file`
   - "Load the uipath-rpa skill" -> `list_skills` -> `read_skill`

   Run the battery before and after the change. Success = every prompt triggers the
   expected tool call.

## 7. Success Criteria

- Every prompt in the Copilot battery triggers the expected tool.
- `dotnet test` passes with no test changes required.
- No diff in any `.cs` file outside the three tool classes, and no logic diff
  inside them (attributes only).
