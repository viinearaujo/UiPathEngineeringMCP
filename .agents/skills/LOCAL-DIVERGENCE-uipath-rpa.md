# Local Divergence — uipath-rpa snapshot

This file is repo-authored and lives OUTSIDE the vendored tree so it survives a
`uip skills install`. A copy is mirrored at `uipath-rpa/LOCAL-DIVERGENCE.md` for
readers browsing the skill; that mirror is overwritten on reinstall, this file is
not. The mirror is `.gitignore`d rather than tracked, because the "Claim changed"
column below quotes XAML-first phrasing and a tracked copy would trip the
verification grep at the bottom of this file.

`.agents/skills/uipath-rpa/` is a vendored snapshot installed from the UiPath skills
marketplace on 2026-07-28 (`.uipath/.skills/manifest.json`). It is patched locally so the
authoring mode default matches this server: **coded-first**. Upstream ships XAML-first,
which is correct for generic UiPath users and wrong here. `README.md`,
`docs/copilot-studio-agent-instructions.txt`, and the `implement_uipath_goal` prompt all
assert coded-first, and `read_skill` / the `uipath://skills/{name}` resource serve these
files to Copilot at runtime.

A future `uip skills install` overwrites that tree and silently restores the XAML-first
claims. Re-apply every row below after any reinstall.

## Patched locations

| File | Claim changed | Now says |
|------|---------------|----------|
| `SKILL.md` § Project Type Detection, item 4 | New project → default to XAML; switch to coded only on explicit coded phrasing | New project → default to coded; XAML for REFramework / orchestration wiring, explicit XAML phrasing, or a XAML-only capability |
| `SKILL.md` § Authoring Mode Selection (lead + scenario table rows for Standard RPA and UI automation) | Default to XAML for new/ambiguous cases; Standard RPA and UI automation are XAML by default | Default to coded for new/ambiguous cases; both rows are Coded by default |
| `references/coded-vs-xaml-guide.md:18` (flowchart step 1, New project) | Default to XAML; "create a workflow" / "automate X" / "build an automation" all mean XAML; coded only on a coded-specific phrase or trigger | Default to coded; those phrasings mean a `.cs` workflow; XAML for REFramework / orchestration wiring, explicit XAML phrasing, or a XAML-only capability |
| `references/coded-vs-xaml-guide.md:19` (flowchart step 2) | Task handleable by existing activities → XAML, "covers the bulk of RPA work" | Same tasks → Coded via the matching `CodedWorkflow` service; XAML on REFramework / orchestration wiring, a XAML-only capability, or an explicit low-code ask |
| `references/coded-vs-xaml-guide.md:24` (flowchart step 7) | Default → XAML | Default → Coded |
| `references/coded-vs-xaml-guide.md:52` (§ Use XAML Workflows When, lead) | XAML is the default for any RPA task; ambiguous tasks resolve to XAML | XAML covers REFramework / orchestration wiring, XAML-only capabilities, and explicit user phrasing; generic requests are coded |
| `references/coded-vs-xaml-guide.md:54` (list item 1) | Standard business automation (Excel, email, PDF, web forms, file ops, connectors) → XAML | Scoped to XAML-only IS connector activities; generic work uses the coded service paths |
| `references/coded-vs-xaml-guide.md:56` (list item 2) | UI automation defaults to XAML; coded `uiAutomation` is the niche path | Coded `uiAutomation` + OR descriptors is the default; XAML UIA activities are the alternative for REFramework wiring or explicitly low-code work |
| `references/coded-vs-xaml-guide.md:58` (list item 3) | Simple linear processes read better as XAML | Scoped to projects that are already XAML or hybrid |
| `references/coded-vs-xaml-guide.md:175` (anti-pattern 5) | Defaulting to coded for ambiguous requests is an anti-pattern | Defaulting to XAML for ambiguous requests is the anti-pattern |
| `references/coded-vs-xaml-guide.md:177` (anti-pattern 7) | Picking coded for UI automation by default is an anti-pattern | Defaulting to XAML for UI automation is the anti-pattern |
| `references/coded/operations-guide.md:7` | Coded procedure ONLY on explicit coded request; ambiguous requests default to XAML | Coded procedure is the default for a new project or an ambiguous request; XAML on the three stated exceptions |
| `references/environment-setup.md:49` | "To work in coded mode", scaffold then add `.cs` files | Coded is the default mode for a new project; scaffold then add `.cs` files |
| `references/environment-setup.md:53` (heading) | For XAML Projects (default for new projects) | For XAML Projects |
| `references/environment-setup.md:72` (heading) | For Coded Projects (only when the user explicitly requested coded) | For Coded Projects |
| `references/project-structure-guide.md:5` — file deleted by the 2026-09-23 refresh, text now at `references/environment-setup.md:339` | For new projects the default is XAML; examples lead with XAML | For new projects the default is coded; examples cover both modes |
| `references/project-structure-guide.md:147` — same, text now at `references/environment-setup.md:481` (decision tree, first question) | New projects default to XAML unless the user said "coded" or named a coded-specific trigger | New projects default to coded unless REFramework / orchestration wiring, a XAML-only capability, or an explicit XAML ask |

## Preserved upstream rules

Unchanged, and still authoritative:

- An explicit user request for a mode is never second-guessed (flowchart step 0; anti-pattern 6).
- An existing project's mode governs — a XAML-only project keeps using XAML (flowchart step 1).
- XAML remains correct for REFramework, queue-based orchestration, retry patterns, and Integration Service connector activities that need XAML-specific dynamic activity config.
- `init` always scaffolds XAML regardless of flags; coded mode is a post-scaffold step. `--expression-language` is independent of the coded vs XAML choice.

## Deliberately untouched

- `references/activity-docs/**` — 619 vendored per-package activity docs. Per-package mode guidance there is not a project-level default.
- `references/legacy/**` — Legacy (.NET Framework 4.6.1) projects are XAML-only; coded-first does not apply.

## Verifying after a reinstall

```
git grep -inE "default to XAML|XAML is the default|mean XAML|Default\*\* → XAML|XAML\*\* \(default\)" -- .agents/skills/uipath-rpa
```

The only expected hit is `references/coded-vs-xaml-guide.md` step 1's XAML-only-project
rule, which is deliberately preserved. Any other hit is a regression to re-patch.
