# Agent Handoff — Studio Fidelity Gap Closure

## Continuation — fidelity plan closed (HEAD `f1b2b56`)

Read this section first. The sections below are the 2026-09-24 00:10 snapshot.

HEAD is `f1b2b56` — "Resolve loop activities by their emitted element name on every catalog path". The working tree was clean. The external plan at `C:\Users\arauj\.cursor\plans\studio_fidelity_gap_closure_055ad035.plan.md` was not updated in-repo; the commits below are the closure record. Do not edit that plan.

Re-verify before trusting a number:

```powershell
cd C:\Users\arauj\Documents\UiPathEngineeringMCP
dotnet build UiPath.Engineering.Mcp.sln --nologo
dotnet test  UiPath.Engineering.Mcp.sln --configuration Release --nologo
```

Pass `--configuration Release` without `--no-build`, or build Release first. The snapshot's 565 / 277 / 431 / 39 counts are not current. The committed eval scorecard at `tests/UiPath.Engineering.Mcp.Tools.Tests/Evals/scorecards/scorecard.md` is 17/17 structural passes and 0 unexpected escape-hatch successes, including the Switch case the snapshot left red.

The snapshot's remaining todos, closed in its order:

- `p1-xmembers` — `decc1f3`. Spec workflow arguments render as `<x:Members>`, and expression rewrites follow a rename.
- `p1-default-xaml` — `46f02ea`. Discovered activities take their property surface from `activities get-default-xaml`. `ActivityCatalog.CommonActivityCard` is the 13-activity allowlist. `MaxPackageQueries` is 32, and packages past that budget are named in a truncation warning.
- `p1-uipath-loops` — `ef6ad53`, then `f1b2b56`. Loop wraps, package stamps, and modern Excel/UIA activities landed first. Spec name and the element name Studio emits now share one index, so `InterruptibleWhile` resolves on `ListActivityCatalog` (including `ActivityCatalog.Fallback` and every merged catalog).
- `p2-schema-soundness` — `0b1fdc6`. `ActivitySchemaReflector` reads the full settable surface through `MetadataLoadContext`. A typo is rejected when that surface is complete.
- `p2-uia-reachable` — `ef6ad53`, with the eval flip in `8e9b6aa`. Eval 08 requires a declared `uix:` namespace, `uix:NApplicationCard` / `uix:NClick` (never `d1p1:`), and a `TODO Indicate` round-trip. Eval 08b still refuses a genuinely unknown activity.
- `p3d-slot-identity` and `p3-blank-template` — `9af918d`. Branch slots keep their identity on read. The blank workflow template has a real display name and import block.
- `p3-viewstate-idref` — `271b973`. `WorkflowViewState` `IdRef`, `HintSize`, and Sequence `ViewState` are emitted.
- `p3-annotations` — `04b6d3d`. The workflow description is read from the root activity annotation Studio writes it on.
- `p4-flowchart-statemachine` — `51bd3f0`. Flowchart and StateMachine author structure-first, and the parser is graph-aware. `0e5431d` recorded the ViewState `Point`/`Size` misclassification as a skipped structural finding; `8a2bc9f` fixed it, so built diagrams pass the catalog guard.
- `p5-activity-metadata` and `p5-knowledge-search` — `3ca1cb4`. `get_activity_metadata`, `search_activity_docs`, and `search_uipath_knowledge` (excerpts), plus `uipath://activity/{projectPath}/{name}` and `uipath://idioms/{name}`.
- `p7-allowedvalues` — `0a13395`. Closed-set parameters advertise `AllowedValuesAttribute`, so the protocol schema emits an enum.
- `p7-progress` — `fa9510a`. Every CLI-backed tool reports progress on the in-flight request token.
- `p7-contract` — `1cbeeba`. `get_code_context` rejects an empty locator before analysis. `generate_documentation` walks the full activity outline. A corrupt plan returns `PLAN_INVALID` instead of throwing `JsonException`. Plan save honors its cancellation token.
- `p8-fidelity-evals` — `8e9b6aa`. `GoldenEvalHarness` parses emitted XAML and asserts shape. The substring `Contains` check is gone, which is what made the earlier P0 defects durable.
- `p9-housekeeping` — already on `b89cf8f`: `FingerprintedCache<T>`, per-key semaphores kept for the cache lifetime, Roslyn cache max 8 (`CSharpAnalysisCache.DefaultMaxEntries`), the repo `McpServerOptions` bridged into the SDK `ServerInfo`, and `docs/tool-api-style-guide.md`. The snapshot's "workers still running" note is stale.

After the snapshot, the vendored skill was repaired and is part of this baseline: `5f3205f` re-applied the coded-first divergence, `37b25b9` added a test that fails if a `uip skills install` restores XAML-first wording, and `b254959` moved the two deleted `project-structure-guide.md` rows to `references/environment-setup.md`.

Still in force:

- Do not run `uip --help`. The self-updater rewrites `.agents/skills/`.
- After any skill reinstall, re-apply `.agents/skills/LOCAL-DIVERGENCE-uipath-rpa.md`. The regression test names the offending file and line.
- The short UIA alias `Click` (for `NClick`) stays off `ListActivityCatalog`. `XamlCatalogGuard` matches element names without a namespace, so indexing the bare alias would accept legacy `ui:Click` and the mobile `Click` as modern `uix:NClick`. The alias remains on `ActivityCatalog.TryGet` only.
- `ModelContextProtocol` is still `2.0.0-preview.3`. That bump is the only item this handoff leaves open, and it is a later, isolated change behind the host tests.
- Do not hard-code a tool count. `CopilotConnectorTools` is the source.
- Two types are named `McpServerOptions`: the repo type in `UiPath.Engineering.Mcp.Core.Configuration`, and the SDK type in `ModelContextProtocol.Server`.

No new phase is opened from this file. The fidelity todos listed in the snapshot are closed.

---

# Agent Handoff — Studio Fidelity Gap Closure

Written 2026-09-24 for an agent starting with clean context.

## Start here

1. **The plan (source of truth for remaining work):**
   `C:\Users\arauj\.cursor\plans\studio_fidelity_gap_closure_055ad035.plan.md`
   That path is OUTSIDE the repo (user home `.cursor`). Do not edit it; read the
   todos and their `status` fields.
2. **Last commit:** `b89cf8f` — "Make XAML authoring Studio-correct and wire the
   unused uip CLI surface". Working tree was clean at handoff.
3. **Baseline (verified by the coordinator immediately before the commit):**

   | Project | Result |
   |---|---|
   | Core.Tests | 565 / 565 |
   | Providers.Tests | 277 / 277 |
   | Tools.Tests | 431 / 433 |
   | Server.Tests | 39 / 39 |

   `dotnet build UiPath.Engineering.Mcp.sln` — clean, 0 warnings
   (`TreatWarningsAsErrors` is on).

   The 2 Tools failures are **expected and owned by a later phase** — do not
   "fix" them ad hoc:
   - `GoldenCompilerEvalTests.Switch_SpecValidatesAndXamlEmits` asserts the literal
     `<Switch x:TypeArguments="Int32"`. The corrected renderer emits `x:Int32`.
   - `GoldenCompilerEvalTests.Scorecard_...` aggregates the above, so 10/11.

## Verify before you trust anything

```powershell
cd C:\Users\arauj\Documents\UiPathEngineeringMCP
dotnet build UiPath.Engineering.Mcp.sln --nologo
dotnet test  UiPath.Engineering.Mcp.sln --configuration Release --nologo
```

**Trap:** always pass `--configuration Release` WITHOUT `--no-build`, or build
Release first. Testing `--no-build` after a Debug build silently runs stale
Release assemblies and reports confidently wrong numbers. This happened once.

## What this commit fixed, and why it mattered

The originating problem: the server silently produced XAML that passed
`validate_project` and `build` while being broken at runtime or unopenable in
Studio. Two P0 classes:

**1. Expression language.** `create_project` defaults `expressionLanguage =
"CSharp"`, and that key is immutable — but the spec path hardcoded VB `[bracket]`
attribute-form bindings regardless. Under C# those deserialize as
`VisualBasicValue<T>`; per the refreshed upstream docs the failure is
`xaml/xaml-basics-and-rules.md:665`: the value is *"not evaluated as an
expression… where the property type accepts a string the text survives as a
literal and the workflow validates, builds, and runs with the wrong value and no
error."* A **silent wrong-value**, not a crash.

Now: `Core/Authoring/ProjectXamlSettings.cs` carries the language; C# emits
`CSharpValue` (read) / `CSharpReference` (write) property elements; `[bracket]`
is rejected with a language-specific fixHint; import blocks are emitted.
`ExpressionValue.cs` holds the shared value-form rules so *what validates is what
renders*.

**2. Container body shapes.** `ForEachRow`, `RetryScope`, `InvokeCode`, `While`,
`DoWhile` were declared `container: true` but had no bespoke renderer, so they hit
`RenderGeneric` — children dumped as direct element children, no
`.Body`/`.ActivityBody`/`ActivityAction` wrappers, and `InvokeCode` never emitted
its `Arguments` dictionary at all. `While`/`DoWhile` take a *single* Activity
body, so 2+ children was a hard XAML error. All well-formed XML, so the round-trip
check passed and the gate stayed green.

Also in the commit: Rule 24 `<Sequence>` wraps on every slot; `Assign` renders its
generic form via a real `TypeArgument` property; `TypeToken` covers the full
11-primitive `x:` set plus `s:`/`sd:` with alias declaration; `ActivitySchema`
gained a body descriptor + CLR types/defaults/allowed-values/directions so
discovered activities stop validating as `valid: true` for any property bag;
`get_analyzer_rules` / `manage_packages` / `get_object_repository` (default
connector) and `run_workflow` / `control_debug_session` (leave-off, fail-closed
behind `UiPathCli:EnableExecution`); Roslyn now compiles the generated
`.local/.codedworkflows` descriptors; gap analysis gained a `readability` category
plus per-gap `confidence`.

## The single most important remaining item

**The eval harness still uses substring matching.** `GoldenEvalHarness.cs`'s
`Author` helper does `xaml.Contains(token, StringComparison.Ordinal)`. That is
structurally why every defect above shipped: eval 03 passes on a bare
`<ui:RetryScope` with no `ActivityBody` wrapper; eval 04 passes on an unwrapped
`If.Then`. **Rebuilding it (todo `p8-fidelity-evals`) is what makes the P0 fixes
durable.** Without it the same class of bug returns. Sequence it AFTER the
remaining authoring work, since structural assertions must encode the *final*
shapes.

## Remaining work, in dependency order

Read each todo's full text in the plan file. Summary:

**Phase 2-4 — authoring chain (`Core/Authoring/**`, `Core/Parsing/**`).** One
coherent unit; do it sequentially, not in parallel:
`p1-xmembers` (no `<x:Members>` → cannot declare arguments; also
`WorkflowSurfaceEditor` shares `TypeToken` and now emits `sd:`/`s:` without a
root-declaration pass) → `p1-default-xaml` (wire `activities get-default-xaml`
so non-card activities get real property surfaces; adopt the 13-activity
`common-activity-card` allowlist; `MaxPackageQueries` is 8 and truncates
silently) → `p1-uipath-loops` (migrate to `InterruptibleWhile`/
`InterruptibleDoWhile`/`ui:ForEach`; fix `PackageId` stamping) →
`p2-schema-soundness` (reflection over activity assemblies) →
`p2-uia-reachable` (**see the contradiction below**) → `p3d-slot-identity`
(parser prerequisite) → `p3-viewstate-idref` → `p3-annotations` →
`p3-blank-template` → `p4-flowchart-statemachine`.

**Phase 5** — `p5-activity-metadata` (SP4), `p5-knowledge-search` (SP6).

**Phase 7** — `p7-allowedvalues` (cheapest win available: the allowed-value lists
already exist at every `ToolArgs.ParseChoice` call site, just not advertised to
the protocol), `p7-progress`, `p7-contract`.

**Phase 8** — `p8-fidelity-evals`. See above.

**Phase 9** — `p9-robustness`, `p9-housekeeping`.

### The UIA contradiction you must resolve (todo `p2-uia-reachable`)

`docs/copilot-studio-skills/rpa-authoring/SKILL.md` instructs Copilot to emit real
`NApplicationCard` / `NClick` / `NTypeInto` / `NGetText` with placeholder
selectors and `TODO Indicate` markers. None are in the activity catalog, so
`validate_activity_spec` returns `SPEC_UNKNOWN_ACTIVITY` and `write_workflow_file`
refuses. Worse, **eval 08 asserts that refusal PASSES** and
`GoldenCompilerEvalTests` asserts a **zero** escape-hatch success rate — so CI
currently locks in the contradiction. The shipped skill and the enforced catalog
disagree on the capability that dominates real RPA work. Fixing it intentionally
invalidates eval 08; update it in Phase 8, do not weaken it.

Related: `ActivityFindParser.InferNamespace` already infers the `uix` namespace,
but `XamlBuilder` only ever declares default/`x`/`ui`/`scg`, so a `uix` element
would serialize as `d1p1:NClick`. LINQ-to-XML cannot represent a colon in an
attribute name — `uix:`-prefixed unknown properties are currently omitted *with a
warning* rather than thrown.

### Parser prerequisite (`p3d-slot-identity`)

Rule 24 wrap *detection* is impossible for slot-named bodies (`If.Then`/
`If.Else`, `Switch.Default` + cases, `TryCatch.Try`/`Catch`/`Finally`,
`ForEach.Body`, `PickBranch`, `NApplicationCard.Body`) because
`XamlActivityLocator` treats attached-property slots as transparent and does not
consume depth — a wrapped branch and a bare one are identical in `ActivityModel`.
Only single-body containers are detectable today. Preserve slot identity
additively, without breaking `If.children` = Then semantics or the structural-path
ID scheme pinned by `XamlActivityLocatorTests`.

## Known traps

- **Never run `--help` probes against `uip`.** Its self-updater rewrote the
  vendored `.agents/skills/` tree mid-session (1.200.1 → 1.202.1), wiping local
  patches and deleting files that `docs/copilot-studio-skills/**` referenced by
  exact path. See below.
- **`.agents/skills/uipath-rpa/` is a vendored upstream snapshot** (installed
  2026-07-28, refreshed 2026-09-23). Upstream ships XAML-first; this server is
  coded-first. Local divergence is recorded at
  `.agents/skills/LOCAL-DIVERGENCE-uipath-rpa.md` — deliberately OUTSIDE the
  vendored tree, because an earlier in-tree copy was destroyed by exactly the
  reinstall it was warning about. Re-apply those rows after any reinstall; the
  file carries a `git grep` command to verify.
- **`tests/**/Evals/**` belongs to Phase 8.** Do not edit it from an
  implementation task, and never weaken or delete an assertion to get green —
  report expected breakage instead.
- **Do not hard-code a tool count in docs.** `README.md` says so explicitly. The
  catalog is still changing; `CopilotConnectorTools` is the single source, and
  `CopilotConnectorDocumentationTests` derives its expectations from it.
- **Two distinct types are named `McpServerOptions`** — the repo's own in
  `UiPath.Engineering.Mcp.Core.Configuration` and the SDK's in
  `ModelContextProtocol.Server`. A `services.Configure<McpServerOptions>` resolves
  to the repo one. The repo values are now bridged into the SDK's `ServerInfo`.
- **`ModelContextProtocol` is still pinned to `2.0.0-preview.3`.** Bumping it is
  deliberately deferred to be done alone, last, behind the host tests — a
  preview→stable change mid-flight would destabilize every other workstream.
- `BoundedCache.DefaultMaxEntries` is shared; lowering the Roslyn compilation
  cache must be done explicitly at its construction site, not via the default.
- **Ignore the `Add-Content : Stream was not readable` noise** in tool output —
  that is the shell harness failing to append to its own capture file, not a test
  or build failure. Confirm with an exit code or a `.trx` logger.

## Coordination notes that worked

Local subagents share ONE working directory. Assign disjoint file ownership
explicitly and forbid cross-edits. Two workers once both had write access to
`FilesystemProvider.cs`; the collision was caught and one redirected before
damage. An aborted worker left the build broken (a duplicate `ArgumentDirection`
enum across `ActivityCatalog.cs` and `ActivitySchema.cs`) — after any abort,
**build before committing**, because parallel workers may be writing against a
tree you just broke.

## Status at handoff

Two background workers were still running when this was written; their output was
committed at `b89cf8f` only if it was already on disk and the build was green.
Check `git status` first — if it shows changes in
`.agents/skills/**` or `docs/copilot-studio-skills/**`, a skill-repair pass was
in flight; if it shows `Core/Caching/**` or `Core/CodeAnalysis/**`, an
infrastructure pass (`p9-housekeeping`: `FingerprintedCache<T>` extraction,
`BoundedCache` semaphore-disposal race, Roslyn cache size, `ServerInfo` bridge,
`docs/tool-api-style-guide.md`) was in flight. Re-run the build and full test
suite before doing anything else.