# Skills Catalog + Allowlisted CLI Access — Design

**Date:** 2026-07-29
**Status:** Approved design, pending implementation plan
**Audience:** single user (project owner) via Microsoft 365 Copilot
**Milestone context:** extends the current 20+-tool server; reuses the existing
`IUiPathCliProvider`, `PathGuard`, and `SecretRedactor` infrastructure

## 1. Problem & Goal

Today the MCP server exposes only narrow slices of the `uip` CLI
(`validate_project`, `create_project` via `rpa init`). Microsoft 365 Copilot has
no way to discover *how* to perform UiPath tasks (the know-how lives in the
`.agents/skills/` catalog that `uip skills` installs) and no way to execute
broader CLI operations when a task calls for them.

Goal: raise Copilot usability by giving it (a) the UiPath skills catalog as
on-demand knowledge and (b) a safely allowlisted `uip` CLI execution path —
without breaking the project's non-negotiable rule against arbitrary shell
execution.

Decisions locked during brainstorming:

- Skills first, then CLI — skills carry zero execution risk and immediate payoff.
- Skills are consumed via **tools** (`list_skills` / `read_skill`), not MCP
  resources or prompts (Copilot Studio resource support is unreliable).
- CLI access is **one tool** (`run_uip_cli`) gated by a config-driven verb
  allowlist, not curated per-capability tools and not raw passthrough.
- Initial allowlist is minimal: `rpa` + `solution`. Expansion is a config edit.
- Mutating subcommands are **blocked by default** and enabled via appsettings
  (`EnableMutatingCommands`), not a per-call confirm flag.

Chosen approach: **A — config-driven allowlist + skills tools** (of three
considered: B hardcoded verb policy in code, rejected because expansion would
need redeploys; C skills as MCP resources, rejected for client support).

## 2. Scope

In scope:

- `SkillsOptions` configuration + `ISkillsProvider` / `SkillsProvider`
- Two new tools: `list_skills`, `read_skill`
- `CliCommandPolicy` (verb/subcommand classification) + `run_uip_cli` tool
- `UiPathCliOptions` extensions: `AllowedVerbs`, read-only subcommand table,
  `EnableMutatingCommands`, output cap
- Unit tests for both features using existing fakes

Out of scope (YAGNI now):

- Additional CLI verbs beyond `rpa`/`solution` (config expansion later)
- Per-call confirmation flows for mutating commands
- MCP resources/prompts for skills
- Skill search/ranking (Copilot reads the catalog descriptions; that is enough)
- Multi-user auth, remote skills roots

## 3. Architecture

Follows the existing Core/Providers/Tools split. Providers do the work, tools
stay thin, config lives in `Core/Configuration`.

### 3.1 Skills catalog

**`SkillsOptions`** (`Core/Configuration/SkillsOptions.cs`):

- `SkillsRoot` — default `.agents/skills`, resolved relative to the server's
  working directory; absolute path also accepted
- `MaxSkillFileBytes` — default 65536; any single file read is capped

**`ISkillsProvider` / `SkillsProvider`** (`Providers/Skills/`):

- `Task<IReadOnlyList<SkillSummary>> ListAsync(CancellationToken)` — enumerates
  `<SkillsRoot>/*/SKILL.md`, parses YAML frontmatter `name` and `description`
  (the format `uip skills` already generates). Falls back to the directory name
  when frontmatter is missing or malformed. Re-scans per call — a directory
  listing plus header parse is cheap; no cache invalidation complexity.
- `Task<string?> ReadAsync(string name, string? file, CancellationToken)` —
  resolves `name` case-insensitively against directory name, then frontmatter
  name. Default file is `SKILL.md`; an optional `file` parameter reads auxiliary
  files inside the same skill directory (many skills keep substance in
  `references/*.md`). All paths are confined to the resolved skill directory —
  `..` segments and absolute paths are rejected. Reads capped at
  `MaxSkillFileBytes` with a truncation marker.

**Tools:**

- `list_skills` → `{ "skills": [ { "name", "description", "directory" } ] }`
- `read_skill(name, file?)` → `{ "name", "file", "content", "truncated" }`;
  unknown name → structured error listing available skill names. Content passes
  through `SecretRedactor` for consistency with other file-reading tools.

**Flow:** Copilot calls `list_skills` → picks a skill from descriptions →
`read_skill("uipath-rpa")` → follows the playbook, calling `run_uip_cli` or the
existing workflow tools to execute.

### 3.2 Allowlisted CLI execution

**`UiPathCliOptions` extensions:**

- `AllowedVerbs` — default `[ "rpa", "solution" ]`
- `ReadOnlySubcommands` — per-verb table. Initial defaults:
  `rpa: [ analyze, validate, build ]` (mirrors what `validate_project` already
  runs today), `solution: [ list, status ]` (the `... list/status --output json`
  read commands documented in the `uipath-solution` skill)
- `EnableMutatingCommands` — bool, default `false`
- `MaxOutputChars` — default 32768 per stream

**`CliCommandPolicy`** (`Core/Configuration` or `Providers/UiPathCli`):

- `Classify(verb, arguments)` → `AllowedReadOnly | AllowedMutating | VerbNotAllowed`
- Classification rule: verb must be in `AllowedVerbs`; first token of
  `arguments` is the subcommand; a subcommand in the read-only table is
  `AllowedReadOnly`; anything else is `AllowedMutating` (**fail closed** —
  unknown subcommands are treated as mutating).
- Expanding later = appsettings edit; no code change.

**`run_uip_cli` tool** (`Tools/RunUiPathCliTool.cs`):

- Parameters: `verb` (string), `arguments` (string, appended verbatim — matches
  the existing `IUiPathCliProvider.RunAsync` shape), optional `workingDirectory`
  (validated by the existing `PathGuard` against allowed roots).
- Execution: classify via `CliCommandPolicy` → `VerbNotAllowed` returns a
  structured error listing allowed verbs → `AllowedMutating` with
  `EnableMutatingCommands=false` returns a structured refusal
  (`MUTATING_COMMAND_DISABLED` with a hint to enable it in appsettings) instead
  of executing → otherwise delegates to the existing
  `IUiPathCliProvider.RunAsync`.
- Response: `{ "verb", "arguments", "exitCode", "success", "stdout", "stderr",
  "durationMs", "truncated" }`. stdout/stderr pass through `SecretRedactor`
  and are capped at `MaxOutputChars` with a truncation marker. Existing timeout
  behavior of the provider is unchanged.

### 3.3 Registration

`Program.cs`: bind `SkillsOptions`, register `ISkillsProvider` →
`SkillsProvider`, register `CliCommandPolicy` as a singleton built from
`UiPathCliOptions`. Tools are picked up automatically by the existing
`WithToolsFromAssembly` call.

## 4. Data flow

```text
M365 Copilot
  -> list_skills / read_skill
       -> SkillsProvider -> .agents/skills/*/SKILL.md -> redacted JSON
  -> run_uip_cli(verb, arguments, workingDirectory?)
       -> CliCommandPolicy.Classify
       -> (refuse | IUiPathCliProvider.RunAsync -> uip.exe)
       -> SecretRedactor -> capped structured JSON
```

## 5. Error handling

Follows the existing `ToolError` / `errorCode` taxonomy:

- `SKILL_NOT_FOUND` — unknown skill name; response lists available names
- `SKILL_PATH_REJECTED` — `file` escapes the skill directory
- `SKILLS_ROOT_MISSING` — configured skills root does not exist (list returns
  this rather than an empty catalog, so misconfiguration is visible)
- `CLI_VERB_NOT_ALLOWED` — verb outside `AllowedVerbs`; response lists them
- `MUTATING_COMMAND_DISABLED` — mutating subcommand while
  `EnableMutatingCommands=false`; hint explains the appsettings toggle
- CLI timeout / non-zero exit — structured result (not an exception), stdout /
  stderr redacted before inclusion

Secrets never leave the server: every response that includes file content or
process output passes through `SecretRedactor`.

## 6. Testing

Unit tests only; no new integration infrastructure.

**Tools tests** (`UiPath.Engineering.Mcp.Tools.Tests`), reusing the existing
CLI fake in `Fakes.cs`:

- allowlisted read-only verb executes and returns structured output
- non-allowlisted verb → `CLI_VERB_NOT_ALLOWED`
- mutating subcommand with flag off → `MUTATING_COMMAND_DISABLED`, fake never
  invoked
- mutating subcommand with flag on → executes
- unknown subcommand of an allowed verb → treated as mutating (fail closed)
- `workingDirectory` outside allowed roots → path-guard rejection
- secret-looking stdout is redacted in the response
- over-limit output is truncated with marker

**Provider tests** (`UiPath.Engineering.Mcp.Providers.Tests`), temp-directory
skill fixtures:

- `ListAsync` parses frontmatter name/description; falls back to directory name
- `ReadAsync` resolves case-insensitively; default file is `SKILL.md`
- auxiliary `file` reads work; `../` and absolute paths rejected
- missing skills root → `SKILLS_ROOT_MISSING`; oversized file → truncated

## 7. Non-goals / explicit roadmap

- Extending `AllowedVerbs` to platform ops (`orx`, `tasks`, `insights`, …) —
  config-only change when wanted
- Mutating-by-default posture
- Skills as MCP resources/prompts if Copilot Studio support matures
