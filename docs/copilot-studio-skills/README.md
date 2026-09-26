# Copilot Studio skill packages

Thin Agent Skills for the UiPath Engineering MCP. Each package is a `SKILL.md` that routes the MCP tools on the Copilot default connector (`CopilotConnectorTools.DefaultNames`). The thick playbook is the vendored `uipath-rpa` tree on the server (`.agents/skills/uipath-rpa/`); Copilot reads a playbook file with `search_knowledge` (`mode=skill`, `query=uipath-rpa`, `file=references/...`).

| Skill | Zip | Also bundles |
|-------|-----|--------------|
| `rpa-authoring` | `rpa-authoring.zip` | `references/activity-spec.md`, `references/idioms/*.md` |
| `guided-implementation-loop` | `guided-implementation-loop.zip` | `references/activity-spec.md`, `references/idioms/*.md` |
| `project-docs` | `project-docs.zip` | — |

The bundled files are listed in [pack.json](pack.json) and copied from [../authoring/activity-spec.md](../authoring/activity-spec.md) (the text the `uipath://authoring/activity-spec` resource serves) and [../copilot-idioms/](../copilot-idioms/). Do **not** zip or upload `.agents/skills/uipath-rpa/`.

## Which harness

Copilot Studio documents skills and memory for agents on the **GitHub Copilot harness**: the agent has Build / Preview / Evaluate / Monitor tabs, and the Build tab has a Skills section. Agents on the standard or Copilot chat harness have no Skills section.

On the GitHub Copilot harness, Preview chats, evaluations, and every MCP tool call consume Copilot Credits (developer and trial environments included from September 1, 2026). The agent's Monitor tab shows consumption. The skills keep calls down: one `check_work` verdict instead of `validate_project` + `get_job` polling + `analyze_project_gaps`, and the grammar and idioms ship in the zip instead of costing a lookup.

When Memory is on, the skills tell Copilot to confirm a remembered `projectPath` or plan state with `analyze_project` / `get_implementation_plan` before acting.

## Pack

From the repository root, in PowerShell 5.1 or 7:

```powershell
./scripts/pack-copilot-skills.ps1
```

It writes `artifacts/copilot-skills/<skill>.zip` (gitignored) with `SKILL.md` at the zip root and the `pack.json` files under their `to` paths:

```text
rpa-authoring.zip
  SKILL.md
  references/activity-spec.md
  references/idioms/coded-workflow-try-log.md
  ...
```

`dotnet test` runs `CopilotStudioSkillsDriftTests`, which fails when a skill names a tool outside `DefaultNames`, a `references/...` path that is neither packed nor in the vendored playbook, or a `pack.json` source that does not exist. Run it before packing.

## Before upload

1. Run the MCP host on `McpServer:ToolSurface=CopilotDefault` (the default).
2. After a catalog change, restart the host and tunnel, then edit the MCP server entry in the agent's Tools dialog so Copilot Studio re-reads `tools/list`. A skill that names a tool Copilot cannot see fails at call time.

## Upload in Copilot Studio

1. Open the agent and select **Build**.
2. In the components panel, select **Skills**.
3. First time: **Add skill** → **Upload a skill**, then drop the zip. Repeat for all three packages.
4. Update: open the existing skill and use **Replace** with the newly packed zip. Do not add a second copy.
5. Confirm each skill's `name` and that the description includes trigger phrases.

## Debug in Preview

Keep **End user preview** off so the activity trace shows beside the chat. For each message it lists the skill that loaded, each tool call with its arguments and result, and error nodes.

- A skill that never loads: its description does not match the words used; a skill that loads too often: its description is too broad.
- An error node naming a tool the connector does not advertise, or a playbook file that is not found: the uploaded zip is stale. Repack and **Replace**.
- Timeouts on long calls: prefer `check_work`; for an authoritative CLI compile, start `validate_project` with `build: true` and poll `get_job`.

## Picker review

The orchestrator matches the user message to a skill description, then the skill names MCP tools. If Studio's tool picker looks noisy, disable **one** overlapping tool in the agent UI **only** when that tool fights the orchestrator (duplicate of the green-gate tool, or a hatch). Do not shrink the server catalog back down.

## After catalog changes

Restart the MCP tunnel/host whenever `CopilotDefault` / `tools/list` changes, repack, **Replace** the skills, then start a new chat with `{PROJECT_PATH}`.
