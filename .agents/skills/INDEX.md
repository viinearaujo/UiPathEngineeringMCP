# UiPath RPA Skills Index

Schema version: 1
Scope: local, RPA-only (UiPath Engineering MCP)

## Local divergence from the vendored snapshot

`uipath-rpa/` is vendored from the UiPath skills marketplace (installed 2026-07-28 per
`.uipath/.skills/manifest.json`). Upstream ships XAML-first; this server is coded-first.
The tree is patched locally and every patched location is recorded in
[LOCAL-DIVERGENCE-uipath-rpa.md](LOCAL-DIVERGENCE-uipath-rpa.md).

A `uip skills install` overwrites `uipath-rpa/` and silently restores the XAML-first
claims, which `read_skill` and the `uipath://skills/{name}` resource then serve to Copilot
at runtime. Re-apply every row of the divergence table after any reinstall. Both this file
and the divergence record are repo-authored and are not overwritten; the in-tree mirror at
`uipath-rpa/LOCAL-DIVERGENCE.md` is.

## uipath-rpa

Always invoke for `.xaml` or `.cs` workflow files. Create, edit, build, run, and debug UiPath RPA. Use Engineering MCP tools for authoring. Maestro, IXP, Insights, Agents, Admin, and Orchestrator publish/deploy are out of scope.

Entry point: [uipath-rpa/SKILL.md](uipath-rpa/SKILL.md)

## guided-implementation-loop

Governed plan → implement → verify loop over the UiPath Engineering MCP tools. Use for multi-step RPA feature work.

Entry point: [guided-implementation-loop/SKILL.md](guided-implementation-loop/SKILL.md)
