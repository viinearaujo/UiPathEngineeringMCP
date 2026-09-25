# Copilot Studio skill packages

Thin Agent Skills for the UiPath Engineering MCP. Each package is a `SKILL.md` that routes **advertised** MCP tools. The thick playbook is the vendored `uipath-rpa` tree on the server (`.agents/skills/uipath-rpa/`). Copilot loads a reference with `read_skill("uipath-rpa", file: "references/...")`.

| Skill | Upload this folder |
|-------|--------------------|
| `rpa-authoring` | `docs/copilot-studio-skills/rpa-authoring/` |
| `guided-implementation-loop` | `docs/copilot-studio-skills/guided-implementation-loop/` |
| `project-docs` | `docs/copilot-studio-skills/project-docs/` |

Do **not** zip or upload `.agents/skills/uipath-rpa/`.

## Before upload

1. Ship the expanded CopilotDefault catalog on the MCP host (unique tools the skills name: spec trio, plan create, docs writes, `read_skill`, and the rest of the Copilot-intended set).
2. Restart the tunnel/host so Copilot's `tools/list` refreshes. Skills that name a tool Copilot cannot see will fail at call time.

## Zip layout

Each archive must contain `SKILL.md` **at the zip root**, not nested in a folder.

```text
rpa-authoring.zip
  SKILL.md
```

Wrong: `rpa-authoring.zip` → `rpa-authoring/SKILL.md`.

From this repo, in PowerShell:

```powershell
$root = "docs/copilot-studio-skills"
@(
  "rpa-authoring",
  "guided-implementation-loop",
  "project-docs"
) | ForEach-Object {
  Compress-Archive -Path "$root/$_/SKILL.md" -DestinationPath "$root/$_.zip" -Force
}
```

Studio also accepts a bare `SKILL.md`. Prefer the zip so the package name matches the skill `name`.

## Upload in Copilot Studio

1. Open the agent (GitHub Copilot harness).
2. Select **Build**.
3. In the components panel, select **Skills**.
4. **Add skill** → **Upload a skill**.
5. Drop the zip (or `SKILL.md`). Repeat for all three packages.
6. Confirm each skill's `name` and that the description includes trigger phrases.

## Picker review

The orchestrator matches the user message to a skill description, then the skill names MCP tools. If Studio's tool picker looks noisy after the catalog expansion, disable **one** overlapping tool in the agent UI **only** when that tool fights the orchestrator (duplicate of an advertised green-gate tool, or a hatch). Do not shrink the server catalog back down.

## After catalog changes

Restart the MCP tunnel/host whenever `CopilotDefault` / `tools/list` changes, then start a new chat with `{PROJECT_PATH}`.
