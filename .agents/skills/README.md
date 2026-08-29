# UiPath RPA skills (Engineering MCP)

This SkillsRoot is **RPA-only**. Copilot and `list_skills` must not see Maestro, IXP, Insights, Agents, Admin, or other product playbooks.

Do **not** reinstall the full marketplace set (`uip skills install` of `@uipath/skills`). That catalog routes across every UiPath product and does not match this server.

## How to use

1. Call `list_skills` (returns this short list) or `read_skill` with a name below.
2. For multi-step implement work, read `guided-implementation-loop` first, then `uipath-rpa` once.
3. Follow MCP tools (`validate_activity_spec` → `build_workflow` / `insert_activities`, `validate_project`). Do not switch products.

## Installed skills

| Skill | When to use | Entry point |
|---|---|---|
| `uipath-rpa` | `.xaml` / `.cs` RPA workflows: author, edit, validate. | [uipath-rpa/SKILL.md](uipath-rpa/SKILL.md) |
| `guided-implementation-loop` | Multi-step implement / plan → verify loop over this MCP. | [guided-implementation-loop/SKILL.md](guided-implementation-loop/SKILL.md) |
| `ccc` | Semantic search of **this Engineering MCP repo** (not Copilot Studio). | [ccc/SKILL.md](ccc/SKILL.md) |
