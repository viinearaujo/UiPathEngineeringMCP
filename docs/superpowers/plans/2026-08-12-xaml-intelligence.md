# P3 XAML Intelligence Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the MCP server stable per-snapshot activity IDs, line numbers, and invocation argument mappings for UiPath XAML workflows, exposed through `find_activity`, `get_workflow_dependencies`, and ID-addressed `edit_workflow_activity` / `insert_activities`.

**Architecture:** A single shared traversal (`XamlActivityLocator`) computes structural-path IDs (`sequence.1/if.1/logmessage.2`) and line numbers for every activity in a parsed XAML document; the parser and the activity editor both consume it so IDs never drift between what tools report and what edits target. `ActivityModel` / `InvokeWorkflowModel` are extended additively; `DependencyGraphBuilder` gains argument mappings and a callers index behind the new `get_workflow_dependencies` tool.

**Tech Stack:** .NET 8, C# 12, `System.Xml.Linq`, ModelContextProtocol SDK, xUnit with hand-written fakes.

**Spec:** `docs/superpowers/specs/2026-08-11-xaml-intelligence-design.md`

## Global Constraints

- IDs are **per-parse-snapshot**: deterministic for identical file content, but structural edits may shift ordinals after the edit point. Every tool output that emits IDs must note this where relevant.
- `ActivityModel` gains `Id`, `ParentId`, `Order`, `Line`, `Children` — additive only. `WorkflowModel.Activities` stays the flat pre-order list. `Children` is `[JsonIgnore]` (in-memory navigation only).
- `localName` is lowercased; the ordinal is 1-based, counted in document order among activity siblings, with a fresh counter per element child-list traversal (transparent containers pass `parentId`/`depth` through but start their own counter).
- Attached-property containers (local name contains `.`) and `XamlWorkflowParser.NonActivityElements` are transparent: recursed without consuming a path segment or depth.
- New error codes in `ToolErrorCodes`: `ACTIVITY_NOT_FOUND`, `ACTIVITY_ID_STALE`, `AMBIGUOUS_ACTIVITY` (SCREAMING_SNAKE, stable public contract). No raw exceptions reach the MCP client.
- All tools validate via `ToolResults.GuardProject` / `GuardAllowedPath` and return the standard `ToolResult` envelope.
- No new NuGet dependencies. Match existing style: file-scoped namespaces, target-typed `new`, collection expressions `[]`.
- Tests: xUnit `[Fact]`/`[Theory]`, hand-written fakes from `tests/UiPath.Engineering.Mcp.Tools.Tests/Fakes.cs`, inline XAML raw-string literals.
- Test commands run from the repo root: `dotnet test tests/UiPath.Engineering.Mcp.Core.Tests/UiPath.Engineering.Mcp.Core.Tests.csproj --filter "FullyQualifiedName~<ClassName>"` (and likewise for the Tools.Tests project).

---

### Task 1: XamlActivityLocator — shared traversal with structural IDs

**Files:**
- Create: `src/UiPath.Engineering.Mcp.Core/Parsing/XamlActivityLocator.cs`
- Test: `tests/UiPath.Engineering.Mcp.Core.Tests/XamlActivityLocatorTests.cs`

**Interfaces:**
- Consumes: `XamlWorkflowParser.NonActivityElements` (internal static `HashSet<string>`, same assembly).
- Produces:
  - `public sealed record LocatedActivity(XElement Element, string Id, string? ParentId, int Order, int Line, int Depth);`
  - `public static class XamlActivityLocator { public static IReadOnlyList<LocatedActivity> Locate(XDocument doc); }`
  - Yields activities in pre-order document order. `Order` is the 0-based pre-order index. `Line` is the 1-based source line (0 when the document was parsed without `LoadOptions.SetLineInfo`).

- [ ] **Step 1: Write the failing tests**

Create `tests/UiPath.Engineering.Mcp.Core.Tests/XamlActivityLocatorTests.cs`:

```csharp
using System.Xml.Linq;
using UiPath.Engineering.Mcp.Core.Parsing;

namespace UiPath.Engineering.Mcp.Core.Tests;

public class XamlActivityLocatorTests {
    private const string MixedXaml = """
        <Activity xmlns="http://schemas.microsoft.com/netfx/2009/xaml/activities"
                  xmlns:ui="http://schemas.uipath.com/workflow/activities"
                  xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <Sequence DisplayName="Main Sequence">
            <Sequence.Variables>
              <Variable x:TypeArguments="x:String" Name="userName" />
            </Sequence.Variables>
            <If DisplayName="If connected">
              <If.Then>
                <ui:LogMessage DisplayName="Log yes" Message="y" />
              </If.Then>
            </If>
            <ui:LogMessage DisplayName="Log done" Message="d" />
          </Sequence>
        </Activity>
        """;

    private const string LinesXaml = """
        <Activity xmlns="http://schemas.microsoft.com/netfx/2009/xaml/activities" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <Sequence DisplayName="Main">
            <WriteLine DisplayName="First" />
            <WriteLine DisplayName="Second" />
          </Sequence>
        </Activity>
        """;

    private static IReadOnlyList<LocatedActivity> Locate(string xaml, LoadOptions options = LoadOptions.None) =>
        XamlActivityLocator.Locate(XDocument.Parse(xaml, options));

    [Fact]
    public void Locate_AssignsStructuralPathIds() {
        var activities = Locate(MixedXaml);

        Assert.Equal(
            ["sequence.1", "sequence.1/if.1", "sequence.1/if.1/logmessage.1", "sequence.1/logmessage.2"],
            activities.Select(a => a.Id).ToArray());
    }

    [Fact]
    public void Locate_OrdinalCountsAllActivitySiblingsNotPerName() {
        // If then LogMessage under the same Sequence: if.1 and logmessage.2 (not logmessage.1).
        var activities = Locate(MixedXaml);

        Assert.Equal("sequence.1/logmessage.2", activities.Single(a => a.Element.Attribute("DisplayName")?.Value == "Log done").Id);
    }

    [Fact]
    public void Locate_TreatsAttachedPropertyContainersAsTransparent() {
        var activities = Locate(MixedXaml);

        // Log yes lives under If.Then (transparent): parent is the If, depth 2, ordinal 1 of the If.Then child list.
        var logYes = activities.Single(a => a.Element.Attribute("DisplayName")?.Value == "Log yes");
        Assert.Equal("sequence.1/if.1", logYes.ParentId);
        Assert.Equal(2, logYes.Depth);
        // Variables under Sequence.Variables never appear.
        Assert.DoesNotContain(activities, a => a.Element.Name.LocalName is "Variable" or "If.Then" or "Sequence.Variables");
    }

    [Fact]
    public void Locate_IsDeterministicAcrossParses() {
        var first = Locate(MixedXaml).Select(a => a.Id).ToArray();
        var second = Locate(MixedXaml).Select(a => a.Id).ToArray();

        Assert.Equal(first, second);
    }

    [Fact]
    public void Locate_ReportsOneBasedLineNumbersWhenLineInfoLoaded() {
        var activities = Locate(LinesXaml, LoadOptions.SetLineInfo);

        Assert.Equal(2, activities.Single(a => a.Id == "sequence.1").Line);
        Assert.Equal(3, activities.Single(a => a.Id == "sequence.1/writeline.1").Line);
        Assert.Equal(4, activities.Single(a => a.Id == "sequence.1/writeline.2").Line);
    }

    [Fact]
    public void Locate_ReportsZeroLineWhenLineInfoNotLoaded() {
        var activities = Locate(LinesXaml);

        Assert.All(activities, a => Assert.Equal(0, a.Line));
    }

    [Fact]
    public void Locate_AssignsPreOrderDocumentOrderIndex() {
        var activities = Locate(MixedXaml);

        Assert.Equal(Enumerable.Range(0, activities.Count).ToArray(), activities.Select(a => a.Order).ToArray());
        Assert.Null(activities[0].ParentId);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/UiPath.Engineering.Mcp.Core.Tests/UiPath.Engineering.Mcp.Core.Tests.csproj --filter "FullyQualifiedName~XamlActivityLocatorTests"`
Expected: FAIL — build error, `XamlActivityLocator` / `LocatedActivity` do not exist.

- [ ] **Step 3: Implement XamlActivityLocator**

Create `src/UiPath.Engineering.Mcp.Core/Parsing/XamlActivityLocator.cs`:

```csharp
using System.Xml;
using System.Xml.Linq;

namespace UiPath.Engineering.Mcp.Core.Parsing;

/// <summary>
/// One activity located in a parsed XAML document, with its computed structural-path ID
/// (e.g. "sequence.1/if.1/logmessage.2"). Each path segment is the lowercased element
/// local name plus a 1-based ordinal counted in document order among the activity
/// siblings of one child-list traversal; attached-property containers (dot-suffixed
/// local names) and XAML primitives are transparent — recursed without consuming a
/// segment or depth, starting a fresh ordinal counter for their own child list.
/// IDs are deterministic per parse snapshot; structural edits may shift ordinals.
/// </summary>
public sealed record LocatedActivity(
    XElement Element,
    string Id,
    string? ParentId,
    int Order,
    int Line,
    int Depth);

/// <summary>
/// Single traversal that classifies elements and assigns activity IDs. Both
/// XamlWorkflowParser and XamlActivityEditor consume this so an ID reported by
/// find_activity always addresses the same element the editor edits.
/// </summary>
public static class XamlActivityLocator {
    public static IReadOnlyList<LocatedActivity> Locate(XDocument doc) {
        var results = new List<LocatedActivity>();
        if (doc.Root is not null) {
            WalkChildren(doc.Root, parentId: null, depth: 0, results);
        }
        return results;
    }

    private static void WalkChildren(XElement parent, string? parentId, int depth, List<LocatedActivity> results) {
        var ordinal = 0;
        foreach (var child in parent.Elements()) {
            var local = child.Name.LocalName;
            if (local.Contains('.') || XamlWorkflowParser.NonActivityElements.Contains(local)) {
                WalkChildren(child, parentId, depth, results);
                continue;
            }

            ordinal++;
            var segment = $"{local.ToLowerInvariant()}.{ordinal}";
            var id = parentId is null ? segment : $"{parentId}/{segment}";
            var line = child is IXmlLineInfo info && info.HasLineInfo() ? info.LineNumber : 0;
            results.Add(new LocatedActivity(child, id, parentId, results.Count, line, depth));
            WalkChildren(child, id, depth + 1, results);
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/UiPath.Engineering.Mcp.Core.Tests/UiPath.Engineering.Mcp.Core.Tests.csproj --filter "FullyQualifiedName~XamlActivityLocatorTests"`
Expected: PASS (7 tests).

- [ ] **Step 5: Commit**

```bash
git add src/UiPath.Engineering.Mcp.Core/Parsing/XamlActivityLocator.cs tests/UiPath.Engineering.Mcp.Core.Tests/XamlActivityLocatorTests.cs
git commit -m "feat(sp3): shared XamlActivityLocator with structural-path activity IDs"
```

---

### Task 2: ActivityModel + parser integration (IDs, lines, hierarchy)

**Files:**
- Modify: `src/UiPath.Engineering.Mcp.Core/Models/ActivityModel.cs` (whole file, 7 lines today)
- Modify: `src/UiPath.Engineering.Mcp.Core/Parsing/XamlWorkflowParser.cs:24-52` (`Parse`), replace `Walk` (lines 98-135)
- Test: `tests/UiPath.Engineering.Mcp.Core.Tests/XamlWorkflowParserTests.cs` (append new facts)

**Interfaces:**
- Consumes: `XamlActivityLocator.Locate(XDocument)` → `IReadOnlyList<LocatedActivity>` (Task 1).
- Produces:
  - `ActivityModel` gains `Id` (`string`, default `""`), `ParentId` (`string?`), `Order` (`int`), `Line` (`int`), `Children` (`List<ActivityModel>`, `[JsonIgnore]`).
  - `WorkflowModel.Activities` stays the flat pre-order list; parents always precede children, and `Children` is wired so a child's entry appears in its parent's `Children`.

- [ ] **Step 1: Write the failing tests**

Append to `tests/UiPath.Engineering.Mcp.Core.Tests/XamlWorkflowParserTests.cs`:

```csharp
    [Fact]
    public void Parse_AssignsIdsParentLinksAndOrder() {
        var model = Parse();

        var sequence = model.Activities.Single(a => a.Type == "Sequence");
        Assert.Equal("sequence.1", sequence.Id);
        Assert.Null(sequence.ParentId);

        var logStart = model.Activities.Single(a => a.DisplayName == "Log start");
        Assert.Equal("sequence.1/logmessage.1", logStart.Id);
        Assert.Equal("sequence.1", logStart.ParentId);

        var tryCatch = model.Activities.Single(a => a.Type == "TryCatch");
        Assert.Equal("sequence.1/trycatch.2", tryCatch.Id);

        var invoke = model.Activities.Single(a => a.Type == "InvokeWorkflowFile");
        Assert.Equal("sequence.1/trycatch.2/invokeworkflowfile.1", invoke.Id);
        Assert.Equal("sequence.1/trycatch.2", invoke.ParentId);

        // Log error sits under TryCatch.Catch > Catch > ActivityAction, all transparent.
        var logError = model.Activities.Single(a => a.DisplayName == "Log error");
        Assert.Equal("sequence.1/trycatch.2/logmessage.1", logError.Id);

        Assert.Equal(Enumerable.Range(0, model.Activities.Count).ToArray(),
            model.Activities.Select(a => a.Order).ToArray());
    }

    [Fact]
    public void Parse_WiresChildrenButKeepsFlatPreOrderList() {
        var model = Parse();

        var sequence = model.Activities.Single(a => a.Id == "sequence.1");
        Assert.Equal(["sequence.1/logmessage.1", "sequence.1/trycatch.2"],
            sequence.Children.Select(c => c.Id).ToArray());
        var tryCatch = model.Activities.Single(a => a.Id == "sequence.1/trycatch.2");
        Assert.Equal(["sequence.1/trycatch.2/invokeworkflowfile.1", "sequence.1/trycatch.2/logmessage.1"],
            tryCatch.Children.Select(c => c.Id).ToArray());
    }

    [Fact]
    public void Parse_ChildrenAreNotSerialized() {
        var model = Parse();

        var json = System.Text.Json.JsonSerializer.Serialize(model.Activities);

        Assert.DoesNotContain("\"Children\"", json);
        Assert.Contains("\"Id\"", json);
        Assert.Contains("\"Line\"", json);
    }

    [Fact]
    public void Parse_ReportsOneBasedLineNumbers() {
        var model = Parse();

        // SampleXaml: "<ui:LogMessage DisplayName=\"Log start\" ... />" is content line 16 of the raw literal.
        var logStart = model.Activities.Single(a => a.DisplayName == "Log start");
        Assert.Equal(16, logStart.Line);
        Assert.All(model.Activities, a => Assert.True(a.Line > 0));
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/UiPath.Engineering.Mcp.Core.Tests/UiPath.Engineering.Mcp.Core.Tests.csproj --filter "FullyQualifiedName~XamlWorkflowParserTests"`
Expected: FAIL — build error, `ActivityModel` has no `Id`/`ParentId`/`Order`/`Line`/`Children`.

- [ ] **Step 3: Extend ActivityModel**

Replace `src/UiPath.Engineering.Mcp.Core/Models/ActivityModel.cs` with:

```csharp
using System.Text.Json.Serialization;

namespace UiPath.Engineering.Mcp.Core.Models;

public sealed class ActivityModel {
    public string Id { get; init; } = string.Empty;
    public string? ParentId { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public int Depth { get; init; }
    public int Order { get; init; }
    public int Line { get; init; }
    [JsonIgnore]
    public List<ActivityModel> Children { get; init; } = [];
}
```

- [ ] **Step 4: Rework the parser to consume the locator**

In `src/UiPath.Engineering.Mcp.Core/Parsing/XamlWorkflowParser.cs`:

1. Change the parse call in `Parse` to request line info:

```csharp
doc = XDocument.Parse(xamlContent, LoadOptions.SetLineInfo);
```

2. Replace `Walk(doc.Root, 0, model);` with `PopulateActivities(doc, model);` and replace the whole `Walk` method with:

```csharp
    private void PopulateActivities(XDocument doc, WorkflowModel model) {
        var byId = new Dictionary<string, ActivityModel>(StringComparer.Ordinal);
        foreach (var located in XamlActivityLocator.Locate(doc)) {
            var element = located.Element;
            var local = element.Name.LocalName;

            if (local == "TryCatch") {
                ExtractTryCatch(element, model);
            } else if (local == "InvokeWorkflowFile") {
                model.InvokeWorkflows.Add(new InvokeWorkflowModel {
                    SourceWorkflow = model.FileName,
                    TargetWorkflow = element.Attribute("WorkflowFileName")?.Value
                        ?? element.Attribute("FileName")?.Value
                        ?? string.Empty,
                    DisplayName = element.Attribute("DisplayName")?.Value ?? string.Empty
                });
            } else if (local == "LogMessage") {
                model.LogMessages.Add(new LogMessageModel {
                    DisplayName = element.Attribute("DisplayName")?.Value ?? string.Empty,
                    Level = element.Attribute("Level")?.Value ?? string.Empty,
                    Message = element.Attribute("Message")?.Value
                        ?? element.Attribute("MessageText")?.Value
                        ?? string.Empty
                });
            }

            var activity = new ActivityModel {
                Id = located.Id,
                ParentId = located.ParentId,
                DisplayName = element.Attribute("DisplayName")?.Value ?? local,
                Type = local,
                Depth = located.Depth,
                Order = located.Order,
                Line = located.Line
            };
            model.Activities.Add(activity);
            byId[located.Id] = activity;
            // Pre-order traversal guarantees the parent was already added.
            if (located.ParentId is not null && byId.TryGetValue(located.ParentId, out var parent)) {
                parent.Children.Add(activity);
            }
        }
    }
```

3. Add the `using System.Xml;` import is NOT needed (no `IXmlLineInfo` here); keep `using System.Xml.Linq;`.

Note: the malformed-XAML early return and all `Extract*` helpers stay untouched.

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/UiPath.Engineering.Mcp.Core.Tests/UiPath.Engineering.Mcp.Core.Tests.csproj --filter "FullyQualifiedName~XamlWorkflowParserTests"`
Expected: PASS — the 4 new tests plus all pre-existing parser tests (depth/outline behavior unchanged).

- [ ] **Step 6: Run the full Core test project**

Run: `dotnet test tests/UiPath.Engineering.Mcp.Core.Tests/UiPath.Engineering.Mcp.Core.Tests.csproj`
Expected: PASS — no regressions in ProjectModelBuilder / gap analyzer / search tests that consume `ActivityModel`.

- [ ] **Step 7: Commit**

```bash
git add src/UiPath.Engineering.Mcp.Core/Models/ActivityModel.cs src/UiPath.Engineering.Mcp.Core/Parsing/XamlWorkflowParser.cs tests/UiPath.Engineering.Mcp.Core.Tests/XamlWorkflowParserTests.cs
git commit -m "feat(sp3): parser emits activity IDs, parent links, order, and line numbers"
```

---

### Task 3: InvokeWorkflowFile argument mappings

**Files:**
- Create: `src/UiPath.Engineering.Mcp.Core/Models/ArgumentMappingModel.cs`
- Modify: `src/UiPath.Engineering.Mcp.Core/Models/InvokeWorkflowModel.cs`
- Modify: `src/UiPath.Engineering.Mcp.Core/Parsing/XamlWorkflowParser.cs` (`PopulateActivities` InvokeWorkflowFile branch from Task 2)
- Test: `tests/UiPath.Engineering.Mcp.Core.Tests/XamlWorkflowParserTests.cs` (append)

**Interfaces:**
- Consumes: the `InvokeWorkflowModel` construction added in Task 2's `PopulateActivities`.
- Produces:
  - `public sealed class ArgumentMappingModel { public string Direction { get; init; } public string TargetArgument { get; init; } public string Expression { get; init; } }` — Direction is `"In"`, `"Out"`, or `"In/Out"`.
  - `InvokeWorkflowModel.ArgumentMappings` (`List<ArgumentMappingModel>`, default `[]`).

- [ ] **Step 1: Write the failing test**

Append to `tests/UiPath.Engineering.Mcp.Core.Tests/XamlWorkflowParserTests.cs`:

```csharp
    [Fact]
    public void Parse_ExtractsInvokeWorkflowArgumentMappings() {
        const string xaml = """
        <Activity xmlns="http://schemas.microsoft.com/netfx/2009/xaml/activities"
                  xmlns:ui="http://schemas.uipath.com/workflow/activities"
                  xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <Sequence DisplayName="Main">
            <ui:InvokeWorkflowFile DisplayName="Invoke child" WorkflowFileName="Child.xaml">
              <ui:InvokeWorkflowFile.Arguments>
                <InArgument x:Key="in_CustomerId">[customerId]</InArgument>
                <OutArgument x:Key="out_Result">[result]</OutArgument>
              </ui:InvokeWorkflowFile.Arguments>
            </ui:InvokeWorkflowFile>
          </Sequence>
        </Activity>
        """;

        var model = Parse(xaml);

        var invoke = Assert.Single(model.InvokeWorkflows);
        Assert.Equal("Child.xaml", invoke.TargetWorkflow);
        Assert.Equal(2, invoke.ArgumentMappings.Count);
        Assert.Contains(invoke.ArgumentMappings,
            m => m.Direction == "In" && m.TargetArgument == "in_CustomerId" && m.Expression == "[customerId]");
        Assert.Contains(invoke.ArgumentMappings,
            m => m.Direction == "Out" && m.TargetArgument == "out_Result" && m.Expression == "[result]");
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/UiPath.Engineering.Mcp.Core.Tests/UiPath.Engineering.Mcp.Core.Tests.csproj --filter "FullyQualifiedName~XamlWorkflowParserTests"`
Expected: FAIL — build error, `ArgumentMappingModel` / `ArgumentMappings` do not exist.

- [ ] **Step 3: Add the model types**

Create `src/UiPath.Engineering.Mcp.Core/Models/ArgumentMappingModel.cs`:

```csharp
namespace UiPath.Engineering.Mcp.Core.Models;

/// <summary>
/// One argument binding on an InvokeWorkflowFile: which argument of the target
/// workflow is wired, in which direction, and the binding expression from the caller.
/// </summary>
public sealed class ArgumentMappingModel {
    public string Direction { get; init; } = string.Empty; // In, Out, In/Out
    public string TargetArgument { get; init; } = string.Empty;
    public string Expression { get; init; } = string.Empty;
}
```

Replace `src/UiPath.Engineering.Mcp.Core/Models/InvokeWorkflowModel.cs` with:

```csharp
namespace UiPath.Engineering.Mcp.Core.Models;

public sealed class InvokeWorkflowModel {
    public string SourceWorkflow { get; init; } = string.Empty;
    public string TargetWorkflow { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public List<ArgumentMappingModel> ArgumentMappings { get; init; } = [];
}
```

- [ ] **Step 4: Extract mappings in the parser**

In `XamlWorkflowParser.PopulateActivities`, change the `InvokeWorkflowFile` branch to call a new helper:

```csharp
            } else if (local == "InvokeWorkflowFile") {
                model.InvokeWorkflows.Add(ExtractInvokeWorkflow(element, model.FileName));
            }
```

Add the helpers to `XamlWorkflowParser`:

```csharp
    private static InvokeWorkflowModel ExtractInvokeWorkflow(XElement element, string sourceFileName) {
        var invoke = new InvokeWorkflowModel {
            SourceWorkflow = sourceFileName,
            TargetWorkflow = element.Attribute("WorkflowFileName")?.Value
                ?? element.Attribute("FileName")?.Value
                ?? string.Empty,
            DisplayName = element.Attribute("DisplayName")?.Value ?? string.Empty
        };

        var container = element.Elements()
            .FirstOrDefault(e => e.Name.LocalName == "InvokeWorkflowFile.Arguments");
        if (container is null) {
            return invoke;
        }

        foreach (var argument in container.Elements()) {
            var direction = argument.Name.LocalName switch {
                "InArgument" => "In",
                "OutArgument" => "Out",
                "InOutArgument" => "In/Out",
                _ => null
            };
            var key = argument.Attributes().FirstOrDefault(a => a.Name.LocalName == "Key")?.Value;
            if (direction is null || key is null) {
                continue;
            }

            invoke.ArgumentMappings.Add(new ArgumentMappingModel {
                Direction = direction,
                TargetArgument = key,
                Expression = ExtractExpressionText(argument)
            });
        }
        return invoke;
    }

    // Simple bindings are literal text ([expr]); VisualBasicReference-style bindings
    // carry the expression on an ExpressionText attribute instead.
    private static string ExtractExpressionText(XElement argument) {
        var expressionText = argument.Descendants()
            .Select(d => d.Attribute("ExpressionText")?.Value)
            .FirstOrDefault(t => t is not null);
        return expressionText ?? argument.Value.Trim();
    }
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/UiPath.Engineering.Mcp.Core.Tests/UiPath.Engineering.Mcp.Core.Tests.csproj --filter "FullyQualifiedName~XamlWorkflowParserTests"`
Expected: PASS (all parser tests).

- [ ] **Step 6: Commit**

```bash
git add src/UiPath.Engineering.Mcp.Core/Models/ArgumentMappingModel.cs src/UiPath.Engineering.Mcp.Core/Models/InvokeWorkflowModel.cs src/UiPath.Engineering.Mcp.Core/Parsing/XamlWorkflowParser.cs tests/UiPath.Engineering.Mcp.Core.Tests/XamlWorkflowParserTests.cs
git commit -m "feat(sp3): extract InvokeWorkflowFile argument mappings"
```

---

### Task 4: Editor ID addressing + structured error codes

**Files:**
- Modify: `src/UiPath.Engineering.Mcp.Core/ToolErrorCodes.cs` (append 3 constants)
- Modify: `src/UiPath.Engineering.Mcp.Core/Parsing/XamlActivityEditor.cs` — rewire `Edit` matching onto the locator, add `EditById`, extend `XamlEditResult`
- Test: `tests/UiPath.Engineering.Mcp.Tools.Tests/EditWorkflowActivityToolTests.cs` (class `XamlActivityEditorTests`, append)

**Interfaces:**
- Consumes: `XamlActivityLocator.Locate` (Task 1).
- Produces:
  - `ToolErrorCodes.ActivityNotFound` = `"ACTIVITY_NOT_FOUND"`, `ToolErrorCodes.ActivityIdStale` = `"ACTIVITY_ID_STALE"`, `ToolErrorCodes.AmbiguousActivity` = `"AMBIGUOUS_ACTIVITY"`.
  - `XamlEditResult` gains two positional record params with defaults: `string? ErrorCode = null`, `string? ResolvedId = null`. Factory signatures unchanged: `Ok(string content, int matchCount)`, `Failure(string error, string? errorCode = null)`.
  - `public static XamlEditResult EditById(string xamlContent, string operation, string activityId, string? activityType = null, string? expectedDisplayName = null, string? fragment = null, string position = Last)` — resolves the ID via the locator, then verifies `activityType` / `expectedDisplayName` when supplied. Unknown ID → `ACTIVITY_NOT_FOUND`; verification mismatch → `ACTIVITY_ID_STALE`.
  - Existing `Edit(...)` (DisplayName path) keeps its signature; 0 matches now carry `ACTIVITY_NOT_FOUND`, multiple matches `AMBIGUOUS_ACTIVITY`, and success carries `ResolvedId`.

- [ ] **Step 1: Write the failing tests**

Append to class `XamlActivityEditorTests` in `tests/UiPath.Engineering.Mcp.Tools.Tests/EditWorkflowActivityToolTests.cs`:

```csharp
    [Fact]
    public void EditById_Replace_TargetsExactlyTheResolvedActivity() {
        const string workflow = """
            <Activity x:Class="Main"
              xmlns="http://schemas.microsoft.com/netfx/2009/xaml/activities"
              xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
              xmlns:ui="http://schemas.uipath.com/workflow/activities">
              <Sequence DisplayName="Main">
                <ui:LogMessage DisplayName="Dup" Message="first" />
                <ui:LogMessage DisplayName="Dup" Message="second" />
              </Sequence>
            </Activity>
            """;

        var result = XamlActivityEditor.EditById(workflow, XamlActivityEditor.Replace,
            "sequence.1/logmessage.2", fragment: "<ui:Comment DisplayName=\"Note\" />");

        Assert.True(result.Success, result.Error);
        Assert.Equal("sequence.1/logmessage.2", result.ResolvedId);
        Assert.Contains("Message=\"first\"", result.UpdatedContent);
        Assert.DoesNotContain("Message=\"second\"", result.UpdatedContent);
        Assert.Contains("<ui:Comment DisplayName=\"Note\"", result.UpdatedContent);
    }

    [Fact]
    public void EditById_UnknownId_ReturnsActivityNotFound() {
        var result = XamlActivityEditor.EditById(Workflow, XamlActivityEditor.Remove, "sequence.9/nope.1");

        Assert.False(result.Success);
        Assert.Equal(ToolErrorCodes.ActivityNotFound, result.ErrorCode);
        Assert.Contains("sequence.9/nope.1", result.Error);
    }

    [Fact]
    public void EditById_TypeMismatch_ReturnsActivityIdStale() {
        // sequence.1 is a Sequence; claiming it is a LogMessage means the snapshot moved.
        var result = XamlActivityEditor.EditById(Workflow, XamlActivityEditor.Remove,
            "sequence.1", activityType: "LogMessage");

        Assert.False(result.Success);
        Assert.Equal(ToolErrorCodes.ActivityIdStale, result.ErrorCode);
        Assert.Contains("find_activity", result.Error);
    }

    [Fact]
    public void EditById_DisplayNameMismatch_ReturnsActivityIdStale() {
        var result = XamlActivityEditor.EditById(Workflow, XamlActivityEditor.Remove,
            "sequence.1", expectedDisplayName: "Renamed since snapshot");

        Assert.False(result.Success);
        Assert.Equal(ToolErrorCodes.ActivityIdStale, result.ErrorCode);
    }

    [Fact]
    public void Edit_NoDisplayNameMatch_CarriesActivityNotFoundCode() {
        var result = XamlActivityEditor.Edit(Workflow, XamlActivityEditor.Remove, "Missing");

        Assert.False(result.Success);
        Assert.Equal(ToolErrorCodes.ActivityNotFound, result.ErrorCode);
    }

    [Fact]
    public void Edit_AmbiguousDisplayName_CarriesAmbiguousActivityCode() {
        const string workflow = """
            <Activity x:Class="Main"
              xmlns="http://schemas.microsoft.com/netfx/2009/xaml/activities"
              xmlns:ui="http://schemas.uipath.com/workflow/activities">
              <Sequence DisplayName="Main">
                <ui:LogMessage DisplayName="Dup" Message="a" />
                <ui:LogMessage DisplayName="Dup" Message="b" />
              </Sequence>
            </Activity>
            """;

        var result = XamlActivityEditor.Edit(workflow, XamlActivityEditor.Remove, "Dup");

        Assert.False(result.Success);
        Assert.Equal(ToolErrorCodes.AmbiguousActivity, result.ErrorCode);
        Assert.Contains("activityId", result.Error);
    }

    [Fact]
    public void Edit_ByDisplayName_ReportsResolvedIdOnSuccess() {
        var result = XamlActivityEditor.Edit(Workflow, XamlActivityEditor.Remove, "Start");

        Assert.True(result.Success, result.Error);
        Assert.Equal("sequence.1/logmessage.1", result.ResolvedId);
    }
```

Also add `using UiPath.Engineering.Mcp.Core;` at the top of the test file (for `ToolErrorCodes`) if not already present.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/UiPath.Engineering.Mcp.Tools.Tests/UiPath.Engineering.Mcp.Tools.Tests.csproj --filter "FullyQualifiedName~XamlActivityEditorTests"`
Expected: FAIL — build error, `EditById` / `ErrorCode` / `ResolvedId` do not exist.

- [ ] **Step 3: Add the error codes**

Append to `src/UiPath.Engineering.Mcp.Core/ToolErrorCodes.cs` (before the closing brace):

```csharp
    public const string ActivityNotFound = "ACTIVITY_NOT_FOUND";
    public const string ActivityIdStale = "ACTIVITY_ID_STALE";
    public const string AmbiguousActivity = "AMBIGUOUS_ACTIVITY";
```

- [ ] **Step 4: Rework XamlActivityEditor**

In `src/UiPath.Engineering.Mcp.Core/Parsing/XamlActivityEditor.cs`:

1. Extend the result record (bottom of file):

```csharp
public sealed record XamlEditResult(
    bool Success, string? UpdatedContent, string? Error, int MatchCount,
    string? ErrorCode = null, string? ResolvedId = null) {
    public static XamlEditResult Ok(string content, int matchCount, string? resolvedId = null) =>
        new(true, content, null, matchCount, null, resolvedId);
    public static XamlEditResult Failure(string error, string? errorCode = null) =>
        new(false, null, error, 0, errorCode);
}
```

2. Change the `Edit` parse to include line info and rewire matching onto the locator. Replace the `FindMatches` call block with:

```csharp
        var matches = XamlActivityLocator.Locate(doc)
            .Where(a => string.Equals(a.Element.Attribute("DisplayName")?.Value, displayName, StringComparison.Ordinal)
                && (activityType is null || string.Equals(a.Element.Name.LocalName, activityType, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (matches.Count == 0) {
            return XamlEditResult.Failure(
                $"No activity found with DisplayName '{displayName}'" +
                (activityType is null ? "." : $" of type '{activityType}'."),
                ToolErrorCodes.ActivityNotFound);
        }
        if (matches.Count > 1) {
            return XamlEditResult.Failure(
                $"Found {matches.Count} activities with DisplayName '{displayName}'. " +
                "Pass activityId to target exactly one (run find_activity to list IDs).",
                ToolErrorCodes.AmbiguousActivity);
        }

        return ApplyEdit(doc, matches[0], operation, fragment, position!);
```

(`doc` is now parsed with `LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo`. Delete the now-unused `FindMatches` method.)

3. Replace the `switch (operation)` block in `Edit` with the `ApplyEdit` call above, and extract the shared edit logic plus `EditById`:

```csharp
    public static XamlEditResult EditById(
        string xamlContent,
        string operation,
        string activityId,
        string? activityType = null,
        string? expectedDisplayName = null,
        string? fragment = null,
        string position = Last) {
        if (string.IsNullOrWhiteSpace(activityId)) {
            return XamlEditResult.Failure("activityId is required to locate the target activity.",
                ToolErrorCodes.InvalidArgument);
        }

        XDocument doc;
        try {
            doc = XDocument.Parse(xamlContent, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
        } catch (Exception ex) when (ex is XmlException or InvalidOperationException) {
            return XamlEditResult.Failure($"XAML parse failure: {ex.Message}");
        }

        var located = XamlActivityLocator.Locate(doc)
            .FirstOrDefault(a => string.Equals(a.Id, activityId, StringComparison.Ordinal));
        if (located is null) {
            return XamlEditResult.Failure($"No activity found with ID '{activityId}'.",
                ToolErrorCodes.ActivityNotFound);
        }

        // Verify the snapshot still matches reality: an ID issued before a structural
        // edit may now resolve to a different activity than the caller intended.
        var local = located.Element.Name.LocalName;
        if (activityType is not null && !string.Equals(local, activityType, StringComparison.OrdinalIgnoreCase)) {
            return XamlEditResult.Failure(
                $"Activity ID '{activityId}' now resolves to a '{local}', not '{activityType}'. " +
                "The file changed since the ID was issued; re-run find_activity for fresh IDs.",
                ToolErrorCodes.ActivityIdStale);
        }
        var actualDisplayName = located.Element.Attribute("DisplayName")?.Value;
        if (expectedDisplayName is not null
            && !string.Equals(actualDisplayName, expectedDisplayName, StringComparison.Ordinal)) {
            return XamlEditResult.Failure(
                $"Activity ID '{activityId}' now resolves to DisplayName '{actualDisplayName}', not '{expectedDisplayName}'. " +
                "The file changed since the ID was issued; re-run find_activity for fresh IDs.",
                ToolErrorCodes.ActivityIdStale);
        }

        return ApplyEdit(doc, located, operation, fragment, position);
    }

    private static XamlEditResult ApplyEdit(
        XDocument doc, LocatedActivity target, string operation, string? fragment, string position) {
        switch (operation) {
            case Remove:
                RemoveElement(target.Element);
                break;

            case Insert:
            case Replace:
                var nodes = ParseFragment(fragment, out var fragmentError);
                if (nodes is null) {
                    return XamlEditResult.Failure(fragmentError!);
                }
                if (operation == Insert) {
                    InsertInto(target.Element, nodes, position == First);
                } else {
                    target.Element.ReplaceWith(nodes);
                }
                break;

            default:
                return XamlEditResult.Failure($"Unknown operation '{operation}'. Use insert, replace, or remove.");
        }

        return XamlEditResult.Ok(Serialize(doc), 1, target.Id);
    }
```

`Edit`'s tail becomes `return ApplyEdit(doc, matches[0], normalizedOperation, fragment, position);` where the operation string is validated by the caller as today (keep the existing `Unknown operation` safety net inside `ApplyEdit`).

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/UiPath.Engineering.Mcp.Tools.Tests/UiPath.Engineering.Mcp.Tools.Tests.csproj --filter "FullyQualifiedName~XamlActivityEditorTests"`
Expected: PASS — 7 new tests plus all pre-existing editor tests (whitespace preservation etc. unchanged).

- [ ] **Step 6: Commit**

```bash
git add src/UiPath.Engineering.Mcp.Core/ToolErrorCodes.cs src/UiPath.Engineering.Mcp.Core/Parsing/XamlActivityEditor.cs tests/UiPath.Engineering.Mcp.Tools.Tests/EditWorkflowActivityToolTests.cs
git commit -m "feat(sp3): edit XAML activities by structural ID with stale-ID verification"
```

---

### Task 5: DependencyGraphBuilder — mappings on edges + callers index

**Files:**
- Modify: `src/UiPath.Engineering.Mcp.Core/Parsing/DependencyGraphBuilder.cs`
- Test: `tests/UiPath.Engineering.Mcp.Core.Tests/DependencyGraphBuilderTests.cs` (append)

**Interfaces:**
- Consumes: `WorkflowModel.InvokeWorkflows` → `InvokeWorkflowModel.ArgumentMappings` (Task 3).
- Produces:
  - `DependencyGraphEdge` gains `DisplayName` (`string`) and `ArgumentMappings` (`List<ArgumentMappingModel>`).
  - `DependencyGraphResult` gains `CallersIndex` (`IReadOnlyDictionary<string, List<DependencyGraphEdge>>`, case-insensitive keys): target file name → edges that invoke it. Resolved and unresolved edges both appear.
  - `Build(IReadOnlyList<WorkflowModel> workflows, string? mainWorkflow)` signature unchanged.

- [ ] **Step 1: Write the failing tests**

Append to `tests/UiPath.Engineering.Mcp.Core.Tests/DependencyGraphBuilderTests.cs` (the file already has `using UiPath.Engineering.Mcp.Core.Models;` and `using UiPath.Engineering.Mcp.Core.Parsing;`, so no qualifications needed):

```csharp
    [Fact]
    public void Build_EdgesCarryDisplayNameAndArgumentMappings() {
        var workflows = new List<WorkflowModel> {
            new() {
                FileName = "Main.xaml",
                InvokeWorkflows = [new InvokeWorkflowModel {
                    SourceWorkflow = "Main.xaml",
                    TargetWorkflow = "Child.xaml",
                    DisplayName = "Invoke child",
                    ArgumentMappings = [new ArgumentMappingModel {
                        Direction = "In", TargetArgument = "in_CustomerId", Expression = "[customerId]"
                    }]
                }]
            },
            new() { FileName = "Child.xaml" }
        };

        var graph = DependencyGraphBuilder.Build(workflows, "Main.xaml");

        var edge = Assert.Single(graph.Edges);
        Assert.Equal("Invoke child", edge.DisplayName);
        var mapping = Assert.Single(edge.ArgumentMappings);
        Assert.Equal("in_CustomerId", mapping.TargetArgument);
        Assert.Equal("[customerId]", mapping.Expression);
    }

    [Fact]
    public void Build_CallersIndexMapsTargetToIncomingEdges() {
        var workflows = new List<WorkflowModel> {
            new() {
                FileName = "Main.xaml",
                InvokeWorkflows = [
                    new InvokeWorkflowModel { SourceWorkflow = "Main.xaml", TargetWorkflow = "Child.xaml" },
                    new InvokeWorkflowModel { SourceWorkflow = "Main.xaml", TargetWorkflow = "Ghost.xaml" }
                ]
            },
            new() {
                FileName = "Other.xaml",
                InvokeWorkflows = [new InvokeWorkflowModel { SourceWorkflow = "Other.xaml", TargetWorkflow = "Child.xaml" }]
            },
            new() { FileName = "Child.xaml" }
        };

        var graph = DependencyGraphBuilder.Build(workflows, "Main.xaml");

        var childCallers = graph.CallersIndex["child.xaml"]; // case-insensitive
        Assert.Equal(2, childCallers.Count);
        Assert.Contains(childCallers, e => e.Source == "Main.xaml");
        Assert.Contains(childCallers, e => e.Source == "Other.xaml");
        // Unresolved targets are indexed too, so callers of missing workflows are visible.
        var ghostCallers = graph.CallersIndex["Ghost.xaml"];
        Assert.Single(ghostCallers);
        Assert.False(ghostCallers[0].IsResolved);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/UiPath.Engineering.Mcp.Core.Tests/UiPath.Engineering.Mcp.Core.Tests.csproj --filter "FullyQualifiedName~DependencyGraphBuilderTests"`
Expected: FAIL — build error, `DisplayName` / `ArgumentMappings` / `CallersIndex` do not exist.

- [ ] **Step 3: Extend the builder**

In `src/UiPath.Engineering.Mcp.Core/Parsing/DependencyGraphBuilder.cs`:

1. Extend the edge and result types:

```csharp
public sealed class DependencyGraphEdge {
    public string Source { get; init; } = string.Empty;
    public string Target { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public bool IsResolved { get; init; }
    public List<ArgumentMappingModel> ArgumentMappings { get; init; } = [];
}

public sealed class DependencyGraphResult {
    public List<DependencyGraphEdge> Edges { get; init; } = [];
    public List<List<string>> Cycles { get; init; } = [];
    public List<string> Orphans { get; init; } = [];
    public IReadOnlyDictionary<string, List<DependencyGraphEdge>> CallersIndex { get; init; }
        = new Dictionary<string, List<DependencyGraphEdge>>(StringComparer.OrdinalIgnoreCase);
}
```

2. In `Build`, populate the new edge fields from the invoke model:

```csharp
                result.Edges.Add(new DependencyGraphEdge {
                    Source = workflow.FileName,
                    Target = invoke.TargetWorkflow,
                    DisplayName = invoke.DisplayName,
                    IsResolved = resolved,
                    ArgumentMappings = [.. invoke.ArgumentMappings]
                });
```

3. After the `foreach (var workflow ...)` edge loop, build the callers index before computing cycles:

```csharp
        var callers = new Dictionary<string, List<DependencyGraphEdge>>(StringComparer.OrdinalIgnoreCase);
        foreach (var edge in result.Edges) {
            if (!callers.TryGetValue(edge.Target, out var incoming)) {
                incoming = [];
                callers[edge.Target] = incoming;
            }
            incoming.Add(edge);
        }

        return new DependencyGraphResult {
            Edges = result.Edges,
            CallersIndex = callers,
            Cycles = DetectCycles(byFileName.Keys, adjacency),
            Orphans = FindOrphans(byFileName.Keys, adjacency, mainWorkflow)
        };
```

(Replace the trailing `result.Cycles.AddRange(...)` / `result.Orphans.AddRange(...)` / `return result;` accordingly, or keep mutation and add `result.CallersIndex = callers;` — either shape is fine as long as `CallersIndex` is populated.)

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/UiPath.Engineering.Mcp.Core.Tests/UiPath.Engineering.Mcp.Core.Tests.csproj --filter "FullyQualifiedName~DependencyGraphBuilderTests"`
Expected: PASS — new tests plus all pre-existing graph tests.

- [ ] **Step 5: Commit**

```bash
git add src/UiPath.Engineering.Mcp.Core/Parsing/DependencyGraphBuilder.cs tests/UiPath.Engineering.Mcp.Core.Tests/DependencyGraphBuilderTests.cs
git commit -m "feat(sp3): dependency graph edges carry argument mappings and a callers index"
```

---

### Task 6: `get_workflow_dependencies` tool

**Files:**
- Create: `src/UiPath.Engineering.Mcp.Tools/GetWorkflowDependenciesTool.cs`
- Test: `tests/UiPath.Engineering.Mcp.Tools.Tests/GetWorkflowDependenciesToolTests.cs`

**Interfaces:**
- Consumes: `IProjectModelBuilder.BuildAsync(projectPath, cancellationToken)` → `UiPathProjectModel` (has `Workflows`, `MainWorkflow`); `DependencyGraphBuilder.Build` returning edges with `DisplayName`/`ArgumentMappings` and `CallersIndex` (Task 5); `ToolResults.GuardAllowedPath`, `ToolResults.Ok`, `ToolResults.FromException`.
- Produces: MCP tool method `GetWorkflowDependencies(string projectPath, string? workflowFile = null, CancellationToken cancellationToken = default)`. Tools are auto-discovered from the Tools assembly — no registration step needed.

- [ ] **Step 1: Write the failing tests**

Create `tests/UiPath.Engineering.Mcp.Tools.Tests/GetWorkflowDependenciesToolTests.cs`:

```csharp
using System.Text.Json;
using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Tools.Tests;

public class GetWorkflowDependenciesToolTests {
    private const string ProjectPath = "/projects/testProcess";

    private static UiPathProjectModel SampleModel() => new() {
        ProjectName = "testProcess",
        MainWorkflow = "Main.xaml",
        Workflows = [
            new WorkflowModel {
                FileName = "Main.xaml",
                InvokeWorkflows = [new InvokeWorkflowModel {
                    SourceWorkflow = "Main.xaml",
                    TargetWorkflow = "Child.xaml",
                    DisplayName = "Invoke child",
                    ArgumentMappings = [new ArgumentMappingModel {
                        Direction = "In", TargetArgument = "in_CustomerId", Expression = "[customerId]"
                    }]
                }]
            },
            new WorkflowModel { FileName = "Child.xaml" },
            new WorkflowModel { FileName = "Orphan.xaml" }
        ]
    };

    private static GetWorkflowDependenciesTool Tool(UiPathProjectModel model) =>
        new(new FakeFilesystemProvider(), new FakeProjectModelBuilder { Model = model });

    [Fact]
    public async Task PerWorkflow_ReturnsCallersAndCalleesWithMappings() {
        var tool = Tool(SampleModel());

        var result = await tool.GetWorkflowDependencies(ProjectPath, "Main.xaml");

        Assert.Equal("success", result.Status);
        var data = JsonSerializer.SerializeToElement(result.Data);
        var callees = data.GetProperty("callees");
        Assert.Equal("Child.xaml", callees[0].GetProperty("targetWorkflow").GetString());
        var mapping = callees[0].GetProperty("argumentMappings")[0];
        Assert.Equal("in_CustomerId", mapping.GetProperty("targetArgument").GetString());
        Assert.Equal("[customerId]", mapping.GetProperty("expression").GetString());
        Assert.Equal(0, data.GetProperty("callers").GetArrayLength());
    }

    [Fact]
    public async Task PerWorkflow_ChildSeesItsCaller() {
        var tool = Tool(SampleModel());

        var result = await tool.GetWorkflowDependencies(ProjectPath, "Child.xaml");

        Assert.Equal("success", result.Status);
        var data = JsonSerializer.SerializeToElement(result.Data);
        var callers = data.GetProperty("callers");
        Assert.Equal(1, callers.GetArrayLength());
        Assert.Equal("Main.xaml", callers[0].GetProperty("sourceWorkflow").GetString());
    }

    [Fact]
    public async Task ProjectWide_ReturnsEdgesCyclesOrphansUnresolved() {
        var tool = Tool(SampleModel());

        var result = await tool.GetWorkflowDependencies(ProjectPath);

        Assert.Equal("success", result.Status);
        var data = JsonSerializer.SerializeToElement(result.Data);
        Assert.Equal(1, data.GetProperty("edges").GetArrayLength());
        Assert.Equal(0, data.GetProperty("cycles").GetArrayLength());
        Assert.Contains(data.GetProperty("orphans").EnumerateArray(),
            o => o.GetString() == "Orphan.xaml");
        Assert.Equal(0, data.GetProperty("unresolved").GetArrayLength());
    }

    [Fact]
    public async Task UnknownWorkflow_ReturnsErrorListingAvailable() {
        var tool = Tool(SampleModel());

        var result = await tool.GetWorkflowDependencies(ProjectPath, "Missing.xaml");

        Assert.Equal("error", result.Status);
        Assert.Contains("Missing.xaml", result.Summary);
        var data = JsonSerializer.SerializeToElement(result.Data);
        Assert.Contains(data.GetProperty("availableWorkflows").EnumerateArray(),
            w => w.GetString() == "Main.xaml");
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/UiPath.Engineering.Mcp.Tools.Tests/UiPath.Engineering.Mcp.Tools.Tests.csproj --filter "FullyQualifiedName~GetWorkflowDependenciesToolTests"`
Expected: FAIL — build error, `GetWorkflowDependenciesTool` does not exist.

- [ ] **Step 3: Implement the tool**

Create `src/UiPath.Engineering.Mcp.Tools/GetWorkflowDependenciesTool.cs`:

```csharp
using System.ComponentModel;
using System.Diagnostics;
using ModelContextProtocol.Server;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Core.Parsing;

namespace UiPath.Engineering.Mcp.Tools;

[McpServerToolType]
public sealed class GetWorkflowDependenciesTool {
    private readonly IFilesystemProvider _filesystem;
    private readonly IProjectModelBuilder _modelBuilder;

    public GetWorkflowDependenciesTool(IFilesystemProvider filesystem, IProjectModelBuilder modelBuilder) {
        _filesystem = filesystem;
        _modelBuilder = modelBuilder;
    }

    [McpServerTool, Description("Shows the InvokeWorkflowFile dependency graph of a UiPath project. With workflowFile: the callers and callees of that workflow, each edge carrying the argument mappings passed at the invoke site. Without workflowFile: the full project edge list plus cycles, orphans (unreachable from Main), and unresolved targets.")]
    public async Task<ToolResult> GetWorkflowDependencies(
        [Description("Absolute path to the UiPath project directory (must contain project.json).")] string projectPath,
        [Description("Optional workflow file name (with or without .xaml). When omitted, the project-wide graph is returned.")] string? workflowFile = null,
        CancellationToken cancellationToken = default) {
        var sw = Stopwatch.StartNew();

        if (ToolResults.GuardProject(_filesystem, projectPath, sw) is { } guardFailure) {
            return guardFailure;
        }

        try {
            var model = await _modelBuilder.BuildAsync(projectPath, cancellationToken);
            var graph = DependencyGraphBuilder.Build(model.Workflows, model.MainWorkflow);

            if (workflowFile is null) {
                return ProjectWide(graph, sw);
            }

            var requestedName = Path.GetFileName(workflowFile);
            if (!requestedName.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)) {
                requestedName += ".xaml";
            }
            var workflow = model.Workflows.FirstOrDefault(w =>
                string.Equals(w.FileName, requestedName, StringComparison.OrdinalIgnoreCase));
            if (workflow is null) {
                return new ToolResult {
                    Status = "error",
                    Summary = $"Workflow '{requestedName}' not found.",
                    Errors = [$"Workflow '{requestedName}' was not found in project '{model.ProjectName}'."],
                    Data = new {
                        availableWorkflows = model.Workflows
                            .Select(w => w.FileName)
                            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                            .ToList()
                    },
                    DurationMs = sw.ElapsedMilliseconds
                };
            }

            var callers = graph.CallersIndex.TryGetValue(workflow.FileName, out var incoming)
                ? incoming
                : [];
            return ToolResults.Ok(
                $"Workflow '{workflow.FileName}': {callers.Count} caller(s), {workflow.InvokeWorkflows.Count} callee(s).",
                new {
                    workflow = workflow.FileName,
                    callers = callers.Select(c => new {
                        sourceWorkflow = c.Source,
                        displayName = c.DisplayName,
                        argumentMappings = MapArguments(c.ArgumentMappings)
                    }).ToList(),
                    callees = workflow.InvokeWorkflows.Select(i => new {
                        targetWorkflow = i.TargetWorkflow,
                        displayName = i.DisplayName,
                        argumentMappings = MapArguments(i.ArgumentMappings)
                    }).ToList()
                }, sw);
        } catch (Exception ex) {
            return ToolResults.FromException(ex, "Dependency analysis failed.", sw);
        }
    }

    private static ToolResult ProjectWide(DependencyGraphResult graph, Stopwatch sw) =>
        ToolResults.Ok(
            $"Dependency graph: {graph.Edges.Count} edge(s), {graph.Cycles.Count} cycle(s), " +
            $"{graph.Orphans.Count} orphan(s), {graph.Edges.Count(e => !e.IsResolved)} unresolved.",
            new {
                edges = graph.Edges.Select(e => new {
                    sourceWorkflow = e.Source,
                    targetWorkflow = e.Target,
                    displayName = e.DisplayName,
                    isResolved = e.IsResolved,
                    argumentMappings = MapArguments(e.ArgumentMappings)
                }).ToList(),
                cycles = graph.Cycles,
                orphans = graph.Orphans,
                unresolved = graph.Edges
                    .Where(e => !e.IsResolved)
                    .Select(e => new { sourceWorkflow = e.Source, targetWorkflow = e.Target })
                    .ToList()
            }, sw);

    private static List<object> MapArguments(List<ArgumentMappingModel> mappings) =>
        mappings.Select(m => (object)new {
            direction = m.Direction,
            targetArgument = m.TargetArgument,
            expression = m.Expression
        }).ToList();
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/UiPath.Engineering.Mcp.Tools.Tests/UiPath.Engineering.Mcp.Tools.Tests.csproj --filter "FullyQualifiedName~GetWorkflowDependenciesToolTests"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/UiPath.Engineering.Mcp.Tools/GetWorkflowDependenciesTool.cs tests/UiPath.Engineering.Mcp.Tools.Tests/GetWorkflowDependenciesToolTests.cs
git commit -m "feat(sp3): get_workflow_dependencies tool with callers, callees, and argument mappings"
```

---

### Task 7: `find_activity` tool

**Files:**
- Create: `src/UiPath.Engineering.Mcp.Tools/FindActivityTool.cs`
- Test: `tests/UiPath.Engineering.Mcp.Tools.Tests/FindActivityToolTests.cs`

**Interfaces:**
- Consumes: `IProjectModelBuilder.BuildAsync` → `UiPathProjectModel.Workflows[].Activities[]` with `Id`/`ParentId`/`Order`/`Line` (Task 2); `ToolResults.GuardProject`.
- Produces: MCP tool method `FindActivity(string projectPath, string? workflowFile = null, string? query = null, string? activityType = null, string? activityId = null, CancellationToken cancellationToken = default)`. Match DTO fields (camelCase in JSON): `id`, `displayName`, `type`, `workflowFile`, `line`, `parentId`, `depth`, `ancestors` (`[{ id, displayName }]` root-first).

- [ ] **Step 1: Write the failing tests**

Create `tests/UiPath.Engineering.Mcp.Tools.Tests/FindActivityToolTests.cs`:

```csharp
using System.Text.Json;
using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Tools.Tests;

public class FindActivityToolTests {
    private const string ProjectPath = "/projects/testProcess";

    private static UiPathProjectModel SampleModel() {
        var sequence = new ActivityModel {
            Id = "sequence.1", DisplayName = "Main Sequence", Type = "Sequence", Depth = 0, Order = 0, Line = 5
        };
        var ifActivity = new ActivityModel {
            Id = "sequence.1/if.1", ParentId = "sequence.1", DisplayName = "If connected",
            Type = "If", Depth = 1, Order = 1, Line = 6
        };
        var log = new ActivityModel {
            Id = "sequence.1/if.1/logmessage.1", ParentId = "sequence.1/if.1", DisplayName = "Log start",
            Type = "LogMessage", Depth = 2, Order = 2, Line = 7
        };
        return new UiPathProjectModel {
            ProjectName = "testProcess",
            MainWorkflow = "Main.xaml",
            Workflows = [
                new WorkflowModel { FileName = "Main.xaml", Activities = [sequence, ifActivity, log] },
                new WorkflowModel {
                    FileName = "Child.xaml",
                    Activities = [new ActivityModel {
                        Id = "sequence.1", DisplayName = "Child seq", Type = "Sequence", Depth = 0, Order = 0, Line = 3
                    }]
                }
            ]
        };
    }

    private static FindActivityTool Tool(UiPathProjectModel model) =>
        new(new FakeFilesystemProvider(), new FakeProjectModelBuilder { Model = model });

    [Fact]
    public async Task Query_FiltersByDisplayNameCaseInsensitively() {
        var result = await Tool(SampleModel()).FindActivity(ProjectPath, query: "log");

        Assert.Equal("success", result.Status);
        var match = JsonSerializer.SerializeToElement(result.Data).GetProperty("matches")[0];
        Assert.Equal("sequence.1/if.1/logmessage.1", match.GetProperty("id").GetString());
        Assert.Equal("Main.xaml", match.GetProperty("workflowFile").GetString());
        Assert.Equal(7, match.GetProperty("line").GetInt32());
        Assert.Equal("sequence.1/if.1", match.GetProperty("parentId").GetString());
        var ancestors = match.GetProperty("ancestors");
        Assert.Equal(2, ancestors.GetArrayLength());
        Assert.Equal("sequence.1", ancestors[0].GetProperty("id").GetString());
        Assert.Equal("sequence.1/if.1", ancestors[1].GetProperty("id").GetString());
    }

    [Fact]
    public async Task ActivityId_LooksUpExactActivity() {
        var result = await Tool(SampleModel()).FindActivity(ProjectPath, activityId: "sequence.1/if.1");

        Assert.Equal("success", result.Status);
        var matches = JsonSerializer.SerializeToElement(result.Data).GetProperty("matches");
        Assert.Equal(1, matches.GetArrayLength());
        Assert.Equal("If", matches[0].GetProperty("type").GetString());
    }

    [Fact]
    public async Task WorkflowFileAndType_NarrowTheSearch() {
        var result = await Tool(SampleModel()).FindActivity(ProjectPath,
            workflowFile: "Child.xaml", activityType: "Sequence");

        Assert.Equal("success", result.Status);
        var matches = JsonSerializer.SerializeToElement(result.Data).GetProperty("matches");
        Assert.Equal(1, matches.GetArrayLength());
        Assert.Equal("Child.xaml", matches[0].GetProperty("workflowFile").GetString());
    }

    [Fact]
    public async Task NoMatches_IsSuccessWithNote() {
        var result = await Tool(SampleModel()).FindActivity(ProjectPath, query: "does-not-exist");

        Assert.Equal("success", result.Status);
        var data = JsonSerializer.SerializeToElement(result.Data);
        Assert.Equal(0, data.GetProperty("matches").GetArrayLength());
        Assert.False(string.IsNullOrEmpty(data.GetProperty("note").GetString()));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/UiPath.Engineering.Mcp.Tools.Tests/UiPath.Engineering.Mcp.Tools.Tests.csproj --filter "FullyQualifiedName~FindActivityToolTests"`
Expected: FAIL — build error, `FindActivityTool` does not exist.

- [ ] **Step 3: Implement the tool**

Create `src/UiPath.Engineering.Mcp.Tools/FindActivityTool.cs`:

```csharp
using System.ComponentModel;
using System.Diagnostics;
using ModelContextProtocol.Server;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Tools;

[McpServerToolType]
public sealed class FindActivityTool {
    private readonly IFilesystemProvider _filesystem;
    private readonly IProjectModelBuilder _modelBuilder;

    public FindActivityTool(IFilesystemProvider filesystem, IProjectModelBuilder modelBuilder) {
        _filesystem = filesystem;
        _modelBuilder = modelBuilder;
    }

    [McpServerTool, Description("Finds activities inside UiPath .xaml workflows and returns their stable activity IDs, line numbers, and ancestor chain. Filter by workflowFile, DisplayName substring (query), exact activity type, or exact activity ID. Pass the returned id to edit_workflow_activity / insert_activities as activityId. IDs are per-parse-snapshot: after a structural edit, re-run find_activity before using IDs captured earlier.")]
    public async Task<ToolResult> FindActivity(
        [Description("Absolute path to the UiPath project directory (must contain project.json).")] string projectPath,
        [Description("Optional workflow file name (with or without .xaml) to limit the search to one workflow.")] string? workflowFile = null,
        [Description("Optional DisplayName substring, case-insensitive.")] string? query = null,
        [Description("Optional exact activity type, e.g. 'LogMessage'.")] string? activityType = null,
        [Description("Optional exact activity ID, e.g. 'sequence.1/if.1'. When supplied, other filters are ignored.")] string? activityId = null,
        CancellationToken cancellationToken = default) {
        var sw = Stopwatch.StartNew();

        if (ToolResults.GuardProject(_filesystem, projectPath, sw) is { } guardFailure) {
            return guardFailure;
        }

        try {
            var model = await _modelBuilder.BuildAsync(projectPath, cancellationToken);

            IEnumerable<WorkflowModel> workflows = model.Workflows.Where(w => !w.HasParseError);
            if (workflowFile is not null) {
                var requestedName = Path.GetFileName(workflowFile);
                if (!requestedName.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)) {
                    requestedName += ".xaml";
                }
                workflows = workflows.Where(w =>
                    string.Equals(w.FileName, requestedName, StringComparison.OrdinalIgnoreCase));
            }

            var matches = new List<object>();
            foreach (var workflow in workflows) {
                var byId = workflow.Activities.ToDictionary(a => a.Id, StringComparer.Ordinal);
                foreach (var activity in workflow.Activities) {
                    if (activityId is not null) {
                        if (!string.Equals(activity.Id, activityId, StringComparison.Ordinal)) {
                            continue;
                        }
                    } else {
                        if (query is not null
                            && !activity.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)) {
                            continue;
                        }
                        if (activityType is not null
                            && !string.Equals(activity.Type, activityType, StringComparison.OrdinalIgnoreCase)) {
                            continue;
                        }
                    }

                    matches.Add(new {
                        id = activity.Id,
                        displayName = activity.DisplayName,
                        type = activity.Type,
                        workflowFile = workflow.FileName,
                        line = activity.Line,
                        parentId = activity.ParentId,
                        depth = activity.Depth,
                        ancestors = AncestorsOf(activity, byId)
                    });
                }
            }

            var note = matches.Count == 0
                ? "No activities matched the filters. Broaden the query or check the workflowFile name."
                : "Activity IDs are per-parse-snapshot; re-run find_activity after structural edits.";
            return ToolResults.Ok(
                matches.Count == 1 ? "1 activity matched." : $"{matches.Count} activities matched.",
                new { matches, note }, sw);
        } catch (Exception ex) {
            return ToolResults.FromException(ex, "Activity search failed.", sw);
        }
    }

    private static List<object> AncestorsOf(ActivityModel activity, Dictionary<string, ActivityModel> byId) {
        var chain = new List<object>();
        var current = activity.ParentId;
        while (current is not null && byId.TryGetValue(current, out var parent)) {
            chain.Add(new { id = parent.Id, displayName = parent.DisplayName });
            current = parent.ParentId;
        }
        chain.Reverse(); // root-first
        return chain;
    }
}
```

(Tool DTOs use explicit camelCase anonymous property names — the codebase convention; `JsonSerializer.SerializeToElement` in tests does not camel-case on its own.)

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/UiPath.Engineering.Mcp.Tools.Tests/UiPath.Engineering.Mcp.Tools.Tests.csproj --filter "FullyQualifiedName~FindActivityToolTests"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/UiPath.Engineering.Mcp.Tools/FindActivityTool.cs tests/UiPath.Engineering.Mcp.Tools.Tests/FindActivityToolTests.cs
git commit -m "feat(sp3): find_activity tool with ID, line, and ancestor output"
```

---

### Task 8: `edit_workflow_activity` / `insert_activities` accept `activityId`

**Files:**
- Modify: `src/UiPath.Engineering.Mcp.Tools/EditWorkflowActivityTool.cs`
- Modify: `src/UiPath.Engineering.Mcp.Tools/InsertActivitiesTool.cs`
- Test: `tests/UiPath.Engineering.Mcp.Tools.Tests/EditActivityByIdToolTests.cs` (new)

**Interfaces:**
- Consumes: `XamlActivityEditor.Edit` / `EditById` with `ErrorCode` / `ResolvedId` (Task 4); `ToolErrorCodes.ActivityNotFound` / `ActivityIdStale` / `AmbiguousActivity` / `InvalidArgument`.
- Produces:
  - `EditWorkflowActivity(string projectPath, string relativePath, string operation, string? displayName = null, string? fragment = null, string? activityType = null, string position = XamlActivityEditor.Last, string? activityId = null)`. At least one of `displayName` / `activityId` is required; when both are supplied, `displayName` is verified against the ID-resolved element (stale detection).
  - `InsertActivities(string projectPath, string relativePath, string specJson, string? displayName = null, string? activityId = null, string position = XamlActivityEditor.Last, string? activityType = null)` — parameter order changes (`specJson` moves before the now-optional `displayName`); MCP binds by name and in-repo tests are updated, so this is safe.
  - Success payloads gain `activityId` (the resolved ID) plus a warning that IDs after the edit point may have shifted.

- [ ] **Step 1: Write the failing tests**

Create `tests/UiPath.Engineering.Mcp.Tools.Tests/EditActivityByIdToolTests.cs`:

```csharp
using System.Text.Json;
using UiPath.Engineering.Mcp.Core;

namespace UiPath.Engineering.Mcp.Tools.Tests;

public class EditActivityByIdToolTests {
    private const string ProjectPath = "/projects/testProcess";

    private const string Workflow = """
        <Activity x:Class="Main"
          xmlns="http://schemas.microsoft.com/netfx/2009/xaml/activities"
          xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
          xmlns:ui="http://schemas.uipath.com/workflow/activities">
          <Sequence DisplayName="Main">
            <ui:LogMessage DisplayName="Start" Message="begin" />
          </Sequence>
        </Activity>
        """;

    private const string AssignSpec = """
        {
          "name": "Sequence",
          "children": [
            { "name": "Assign", "properties": { "DisplayName": "Set total", "To": "[total]", "Value": "[42]" } }
          ]
        }
        """;

    private static string Target(string relative) =>
        Path.Combine(Path.GetFullPath(ProjectPath), relative.Replace('/', Path.DirectorySeparatorChar));

    private static (FakeFilesystemProvider Fs, string Path) FilesystemWithWorkflow() {
        var fs = new FakeFilesystemProvider();
        var target = Target("Main.xaml");
        fs.FileContents[target] = Workflow;
        return (fs, target);
    }

    [Fact]
    public void EditWorkflowActivity_ById_ReplacesAndReportsId() {
        var (fs, target) = FilesystemWithWorkflow();
        var tool = new EditWorkflowActivityTool(fs);

        var result = tool.EditWorkflowActivity(ProjectPath, "Main.xaml", "replace",
            activityId: "sequence.1/logmessage.1",
            fragment: "<ui:Comment DisplayName=\"Note\" />");

        Assert.Equal("success", result.Status);
        var data = JsonSerializer.SerializeToElement(result.Data);
        Assert.Equal("sequence.1/logmessage.1", data.GetProperty("activityId").GetString());
        Assert.Contains("<ui:Comment DisplayName=\"Note\"", fs.Writes[target]);
        Assert.Contains(result.Warnings, w => w.Contains("find_activity"));
    }

    [Fact]
    public void EditWorkflowActivity_ByIdWithMismatchedType_ReturnsStaleError() {
        var (fs, _) = FilesystemWithWorkflow();
        var tool = new EditWorkflowActivityTool(fs);

        var result = tool.EditWorkflowActivity(ProjectPath, "Main.xaml", "remove",
            activityId: "sequence.1", activityType: "LogMessage");

        Assert.Equal("error", result.Status);
        var error = Assert.Single(result.ErrorDetails, e => e.ErrorCode == ToolErrorCodes.ActivityIdStale);
        Assert.Equal("find_activity", error.SuggestedTool);
        Assert.Empty(fs.Writes);
    }

    [Fact]
    public void EditWorkflowActivity_ByIdWithMatchingDisplayName_Succeeds() {
        var (fs, _) = FilesystemWithWorkflow();
        var tool = new EditWorkflowActivityTool(fs);

        var result = tool.EditWorkflowActivity(ProjectPath, "Main.xaml", "remove",
            activityId: "sequence.1/logmessage.1", displayName: "Start");

        Assert.Equal("success", result.Status);
        Assert.DoesNotContain("DisplayName=\"Start\"", fs.Writes[Target("Main.xaml")]);
    }

    [Fact]
    public void EditWorkflowActivity_NeitherIdNorDisplayName_ReturnsInvalidArgument() {
        var (fs, _) = FilesystemWithWorkflow();
        var tool = new EditWorkflowActivityTool(fs);

        var result = tool.EditWorkflowActivity(ProjectPath, "Main.xaml", "remove");

        Assert.Equal("error", result.Status);
        Assert.Contains(result.ErrorDetails, e => e.ErrorCode == ToolErrorCodes.InvalidArgument);
        Assert.Empty(fs.Writes);
    }

    [Fact]
    public void EditWorkflowActivity_AmbiguousDisplayName_ReturnsStructuredCode() {
        var fs = new FakeFilesystemProvider();
        fs.FileContents[Target("Main.xaml")] = """
            <Activity x:Class="Main"
              xmlns="http://schemas.microsoft.com/netfx/2009/xaml/activities"
              xmlns:ui="http://schemas.uipath.com/workflow/activities">
              <Sequence DisplayName="Main">
                <ui:LogMessage DisplayName="Dup" Message="a" />
                <ui:LogMessage DisplayName="Dup" Message="b" />
              </Sequence>
            </Activity>
            """;
        var tool = new EditWorkflowActivityTool(fs);

        var result = tool.EditWorkflowActivity(ProjectPath, "Main.xaml", "remove", displayName: "Dup");

        Assert.Equal("error", result.Status);
        Assert.Contains(result.ErrorDetails, e => e.ErrorCode == ToolErrorCodes.AmbiguousActivity);
    }

    [Fact]
    public void InsertActivities_ById_InsertsIntoResolvedContainer() {
        var (fs, target) = FilesystemWithWorkflow();
        var tool = new InsertActivitiesTool(fs);

        var result = tool.InsertActivities(ProjectPath, "Main.xaml", AssignSpec,
            activityId: "sequence.1");

        Assert.Equal("success", result.Status);
        Assert.Contains("DisplayName=\"Set total\"", fs.Writes[target]);
    }

    [Fact]
    public void InsertActivities_NeitherIdNorDisplayName_ReturnsInvalidArgument() {
        var (fs, _) = FilesystemWithWorkflow();
        var tool = new InsertActivitiesTool(fs);

        var result = tool.InsertActivities(ProjectPath, "Main.xaml", AssignSpec);

        Assert.Equal("error", result.Status);
        Assert.Contains(result.ErrorDetails, e => e.ErrorCode == ToolErrorCodes.InvalidArgument);
    }
}
```

Note: the existing `InsertActivitiesToolTests.cs` calls `tool.InsertActivities(ProjectPath, "Main.xaml", "Main", AssignSpec)` positionally — update those call sites to the new parameter order (`AssignSpec` third, `"Main"` as `displayName:`) in Step 4.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/UiPath.Engineering.Mcp.Tools.Tests/UiPath.Engineering.Mcp.Tools.Tests.csproj --filter "FullyQualifiedName~EditActivityByIdToolTests"`
Expected: FAIL — build error, the tool signatures have no `activityId` parameter.

- [ ] **Step 3: Update EditWorkflowActivityTool**

In `src/UiPath.Engineering.Mcp.Tools/EditWorkflowActivityTool.cs`, add `using UiPath.Engineering.Mcp.Core;` and change the tool method:

```csharp
    [McpServerTool, Description("Edits a single activity inside an existing .xaml workflow: insert an activity fragment into a container, replace an activity, or remove one. Target the activity by activityId (preferred, from find_activity) or by DisplayName. Use this for surgical changes instead of rewriting the whole file.")]
    public ToolResult EditWorkflowActivity(
        [Description("Absolute path to the UiPath project directory (must contain project.json).")] string projectPath,
        [Description("Path of the .xaml file relative to the project root, e.g. 'Main.xaml'.")] string relativePath,
        [Description("Operation to perform: insert, replace, or remove.")] string operation,
        [Description("DisplayName of the activity to target (for insert: the container). Optional when activityId is supplied; when both are supplied the DisplayName is verified against the ID-resolved activity.")] string? displayName = null,
        [Description("XAML fragment for insert/replace, e.g. '<ui:LogMessage DisplayName=\"Log\" Message=\"Hi\" />'. Unprefixed WF activities and the ui:/x: prefixes are understood without declarations.")] string? fragment = null,
        [Description("Optional activity type (e.g. 'Sequence') to disambiguate when several activities share the DisplayName.")] string? activityType = null,
        [Description("For insert only: where to add the fragment inside the container — first or last (default).")] string position = XamlActivityEditor.Last,
        [Description("Activity ID from find_activity, e.g. 'sequence.1/if.1' — the preferred way to target an activity.")] string? activityId = null) {

        var sw = Stopwatch.StartNew();

        if (ToolResults.GuardProject(_filesystem, projectPath, sw) is { } guardFailure) {
            return guardFailure;
        }

        if (string.IsNullOrWhiteSpace(relativePath)
            || !relativePath.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)) {
            return ToolResults.Failure("relativePath must point to a .xaml file.", sw);
        }

        if (string.IsNullOrWhiteSpace(activityId) && string.IsNullOrWhiteSpace(displayName)) {
            return ToolResults.Failure(new ToolError(
                ToolErrorCodes.InvalidArgument,
                "Pass activityId (from find_activity) or displayName to locate the target activity.",
                "Run find_activity to list activity IDs."), sw);
        }

        var normalizedOperation = operation?.Trim().ToLowerInvariant();
        if (normalizedOperation is not (XamlActivityEditor.Insert or XamlActivityEditor.Replace or XamlActivityEditor.Remove)) {
            return ToolResults.Failure("operation must be insert, replace, or remove.", sw);
        }

        var normalizedPosition = position?.Trim().ToLowerInvariant();
        if (normalizedPosition is not (XamlActivityEditor.First or XamlActivityEditor.Last)) {
            return ToolResults.Failure("position must be first or last.", sw);
        }

        if (!ToolResults.TryResolveWithinProject(projectPath, relativePath, out var targetPath)) {
            return ToolResults.Failure("relativePath must resolve to a location inside the project directory.", sw);
        }

        if (!_filesystem.FileExists(targetPath)) {
            return ToolResults.Failure($"File not found: {targetPath}", sw);
        }

        var original = _filesystem.ReadAllText(targetPath);
        var edit = string.IsNullOrWhiteSpace(activityId)
            ? XamlActivityEditor.Edit(original, normalizedOperation!, displayName!,
                activityType, fragment, normalizedPosition!)
            : XamlActivityEditor.EditById(original, normalizedOperation!, activityId,
                activityType, displayName, fragment, normalizedPosition!);

        if (!edit.Success) {
            return ToFailure(edit, sw);
        }

        _filesystem.WriteAllText(targetPath, edit.UpdatedContent!);

        return ToolResults.Ok(
            $"Activity '{edit.ResolvedId}' {Describe(normalizedOperation!)} in '{relativePath}'.",
            new {
                filePath = targetPath,
                operation = normalizedOperation,
                activityId = edit.ResolvedId,
                targetDisplayName = displayName
            }, sw,
            warnings: ["Activity IDs are per-parse-snapshot: IDs after the edit point may have shifted. Re-run find_activity before follow-up edits."]);
    }

    // Maps the editor's structured failure codes to the ToolError contract.
    internal static ToolResult ToFailure(XamlEditResult edit, Stopwatch sw) {
        if (edit.ErrorCode is null) {
            return ToolResults.Failure(edit.Error!, sw);
        }
        var fixHint = edit.ErrorCode switch {
            ToolErrorCodes.ActivityNotFound => "Run find_activity to list valid activity IDs and display names.",
            ToolErrorCodes.ActivityIdStale => "Re-run find_activity to get fresh IDs, then retry.",
            ToolErrorCodes.AmbiguousActivity => "Pass activityId to target exactly one activity; run find_activity to get IDs.",
            _ => "Correct the arguments and retry."
        };
        return ToolResults.Failure(
            new ToolError(edit.ErrorCode, edit.Error!, fixHint, "find_activity"), sw);
    }
```

`Describe` stays as-is.

- [ ] **Step 4: Update InsertActivitiesTool and its existing tests**

In `src/UiPath.Engineering.Mcp.Tools/InsertActivitiesTool.cs`, add `using UiPath.Engineering.Mcp.Core;` and change the signature and dispatch:

```csharp
    public ToolResult InsertActivities(
        [Description("Absolute path to the UiPath project directory (must contain project.json).")] string projectPath,
        [Description("Path of the .xaml file relative to the project root, e.g. 'Main.xaml'.")] string relativePath,
        [Description("JSON activity spec describing what to insert, e.g. { \"name\": \"Sequence\", \"children\": [...] }. Run validate_activity_spec on it first.")] string specJson,
        [Description("DisplayName of the container activity that receives the new activities. Optional when activityId is supplied.")] string? displayName = null,
        [Description("Activity ID of the container, from find_activity — the preferred way to target it.")] string? activityId = null,
        [Description("Where to add the activities inside the container — first or last (default).")] string position = XamlActivityEditor.Last,
        [Description("Optional activity type (e.g. 'Sequence') to disambiguate when several activities share the DisplayName.")] string? activityType = null) {
```

Inside the method, after the existing guard/`relativePath` checks, add:

```csharp
        if (string.IsNullOrWhiteSpace(activityId) && string.IsNullOrWhiteSpace(displayName)) {
            return ToolResults.Failure(new ToolError(
                ToolErrorCodes.InvalidArgument,
                "Pass activityId (from find_activity) or displayName to locate the target container.",
                "Run find_activity to list activity IDs."), sw);
        }
```

Replace the `XamlActivityEditor.Edit(...)` call and failure handling with:

```csharp
        var original = _filesystem.ReadAllText(targetPath);
        var edit = string.IsNullOrWhiteSpace(activityId)
            ? XamlActivityEditor.Edit(original, XamlActivityEditor.Insert, displayName!,
                activityType, fragment, normalizedPosition!)
            : XamlActivityEditor.EditById(original, XamlActivityEditor.Insert, activityId,
                activityType, displayName, fragment, normalizedPosition!);

        if (!edit.Success) {
            return EditWorkflowActivityTool.ToFailure(edit, sw);
        }

        _filesystem.WriteAllText(targetPath, edit.UpdatedContent!);

        return ToolResults.Ok(
            $"Spec-based activities inserted into '{edit.ResolvedId}' in '{relativePath}'.",
            new {
                filePath = targetPath,
                operation = XamlActivityEditor.Insert,
                activityId = edit.ResolvedId,
                targetDisplayName = displayName
            }, sw,
            warnings: ["Activity IDs are per-parse-snapshot: IDs after the edit point may have shifted. Re-run find_activity before follow-up edits."]);
```

Update the `InsertActivities` description attribute to mention `activityId` as the preferred targeting input.

Then fix existing call sites in `tests/UiPath.Engineering.Mcp.Tools.Tests/InsertActivitiesToolTests.cs` for the new parameter order, e.g.:

```csharp
var result = tool.InsertActivities(ProjectPath, "Main.xaml", AssignSpec, displayName: "Main");
```

(and the same reordering for the other five calls in that file; the `"Missing"` and `Main.cs` cases keep their semantics).

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/UiPath.Engineering.Mcp.Tools.Tests/UiPath.Engineering.Mcp.Tools.Tests.csproj --filter "FullyQualifiedName~EditActivityByIdToolTests|FullyQualifiedName~InsertActivitiesToolTests|FullyQualifiedName~XamlActivityEditorTests"`
Expected: PASS — new tests plus the updated existing ones.

- [ ] **Step 6: Commit**

```bash
git add src/UiPath.Engineering.Mcp.Tools/EditWorkflowActivityTool.cs src/UiPath.Engineering.Mcp.Tools/InsertActivitiesTool.cs tests/UiPath.Engineering.Mcp.Tools.Tests/EditActivityByIdToolTests.cs tests/UiPath.Engineering.Mcp.Tools.Tests/InsertActivitiesToolTests.cs
git commit -m "feat(sp3): edit and insert tools target activities by ID with structured errors"
```

---

### Task 9: `search_codebase` activity hits gain `id` and `line`

**Files:**
- Modify: `src/UiPath.Engineering.Mcp.Core/CodeSearch/CodebaseSearchDtos.cs` (`ActivityMatch`)
- Modify: `src/UiPath.Engineering.Mcp.Core/CodeSearch/CodebaseSearchService.cs:142-148` (match construction) and `:165` (remove SP2 note)
- Test: `tests/UiPath.Engineering.Mcp.Core.Tests/SearchActivitiesWorkflowsTests.cs`

**Interfaces:**
- Consumes: `ActivityModel.Id` / `Line` (Task 2).
- Produces: `ActivityMatch` gains `Id` (`string`) and `Line` (`int`). The note "Activity hits locate the workflow file; line-level activity addressing lands with SP3." is deleted.

- [ ] **Step 1: Update the tests first**

In `tests/UiPath.Engineering.Mcp.Core.Tests/SearchActivitiesWorkflowsTests.cs`:

1. The test model is hand-crafted (no parser involved), so give the `Main.xaml` activities explicit IDs and lines in `BuildModel()`:

```csharp
                Activities = [
                    new ActivityModel { Id = "sequence.1/logmessage.1", DisplayName = "Log start", Type = "LogMessage", Depth = 1, Line = 12 },
                    new ActivityModel { Id = "sequence.1/logmessage.2", DisplayName = "Log", Type = "LogMessage", Depth = 1, Line = 13 },
                    new ActivityModel { Id = "sequence.1/writeline.3", DisplayName = "Write line", Type = "WriteLine", Depth = 2, Line = 14 }
                ]
```

2. In `SearchActivities_MatchesDisplayNameAndTypeAcrossWorkflows`, delete the SP2 limitation assertion at line 67 (`Assert.Contains("line-level activity addressing", result.Note);`) and add in its place:

```csharp
        var logStart = result.Matches.Single(m => m.DisplayName == "Log start");
        Assert.Equal("sequence.1/logmessage.1", logStart.Id);
        Assert.Equal(12, logStart.Line);
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/UiPath.Engineering.Mcp.Core.Tests/UiPath.Engineering.Mcp.Core.Tests.csproj --filter "FullyQualifiedName~SearchActivitiesWorkflowsTests"`
Expected: FAIL — build error, `ActivityMatch` has no `Id`/`Line` (and the note assertion would fail at runtime once compiled).

- [ ] **Step 3: Implement**

In `CodebaseSearchDtos.cs`:

```csharp
public sealed class ActivityMatch {
    public string Id { get; init; } = string.Empty;
    public string WorkflowFile { get; init; } = string.Empty;
    public string WorkflowPath { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string ActivityType { get; init; } = string.Empty;
    public int Depth { get; init; }
    public int Line { get; init; }
}
```

In `CodebaseSearchService.SearchActivitiesAsync`, populate the new fields in the match construction:

```csharp
                matches.Add((new ActivityMatch {
                    Id = activity.Id,
                    WorkflowFile = workflow.FileName,
                    WorkflowPath = workflow.FilePath,
                    DisplayName = activity.DisplayName,
                    ActivityType = activity.Type,
                    Depth = activity.Depth,
                    Line = activity.Line
                }, exact));
```

Delete the `notes.Add("Activity hits locate the workflow file; line-level activity addressing lands with SP3.");` line and its surrounding `if` if it becomes empty (check lines 160-166 — keep the parse-error note).

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/UiPath.Engineering.Mcp.Core.Tests/UiPath.Engineering.Mcp.Core.Tests.csproj --filter "FullyQualifiedName~SearchActivitiesWorkflowsTests"`
Expected: PASS. Then run the Tools.Tests search suite to confirm the DTO change broke nothing:
Run: `dotnet test tests/UiPath.Engineering.Mcp.Tools.Tests/UiPath.Engineering.Mcp.Tools.Tests.csproj --filter "FullyQualifiedName~SearchCodebaseToolTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/UiPath.Engineering.Mcp.Core/CodeSearch/CodebaseSearchDtos.cs src/UiPath.Engineering.Mcp.Core/CodeSearch/CodebaseSearchService.cs tests/UiPath.Engineering.Mcp.Core.Tests/SearchActivitiesWorkflowsTests.cs
git commit -m "feat(sp3): search_codebase activity hits carry activity ID and line"
```

---

### Task 10: `explain_workflow` `includeActivityTree`

**Files:**
- Modify: `src/UiPath.Engineering.Mcp.Tools/ExplainWorkflowTool.cs`
- Test: `tests/UiPath.Engineering.Mcp.Tools.Tests/ExplainWorkflowToolTests.cs` (append)

**Interfaces:**
- Consumes: `ActivityModel.Children` / `ParentId` (Task 2).
- Produces: `ExplainWorkflow(string projectPath, string workflowFile, bool includeActivityTree = false, CancellationToken cancellationToken = default)`. When true, the XAML-workflow `Data` gains `activityTree`: a nested list of `{ id, displayName, type, line, children }` rooted at activities with `ParentId == null`. Default behavior is byte-identical to today.

- [ ] **Step 1: Write the failing tests**

Append to `tests/UiPath.Engineering.Mcp.Tools.Tests/ExplainWorkflowToolTests.cs`:

```csharp
    [Fact]
    public async Task ExplainWorkflow_WithActivityTree_NestsChildren() {
        // Build the model through the real parser so IDs/Children are wired.
        const string xaml = """
            <Activity xmlns="http://schemas.microsoft.com/netfx/2009/xaml/activities"
                      xmlns:ui="http://schemas.uipath.com/workflow/activities"
                      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <Sequence DisplayName="Main">
                <If DisplayName="Check">
                  <If.Then>
                    <ui:LogMessage DisplayName="Log yes" Message="y" />
                  </If.Then>
                </If>
              </Sequence>
            </Activity>
            """;
        var workflow = new Core.Parsing.XamlWorkflowParser().Parse("Main.xaml", "/proj/Main.xaml", xaml);
        var builder = new FakeProjectModelBuilder {
            Model = new UiPathProjectModel {
                ProjectName = "testProcess",
                MainWorkflow = "Main.xaml",
                Workflows = [workflow]
            }
        };
        var tool = new ExplainWorkflowTool(new FakeFilesystemProvider(), builder);

        var result = await tool.ExplainWorkflow("/projects/testProcess", "Main.xaml", includeActivityTree: true);

        Assert.Equal("success", result.Status);
        var tree = JsonSerializer.SerializeToElement(result.Data).GetProperty("activityTree");
        var root = tree[0];
        Assert.Equal("sequence.1", root.GetProperty("id").GetString());
        var ifNode = root.GetProperty("children")[0];
        Assert.Equal("sequence.1/if.1", ifNode.GetProperty("id").GetString());
        var logNode = ifNode.GetProperty("children")[0];
        Assert.Equal("sequence.1/if.1/logmessage.1", logNode.GetProperty("id").GetString());
        Assert.True(logNode.GetProperty("line").GetInt32() > 0);
    }

    [Fact]
    public async Task ExplainWorkflow_WithoutFlag_ActivityTreeIsNull() {
        var tool = new ExplainWorkflowTool(new FakeFilesystemProvider { Allowed = true },
            new FakeProjectModelBuilder { Model = BuildModel() });

        var result = await tool.ExplainWorkflow("/projects/testProcess", "Main.xaml");

        Assert.Equal("success", result.Status);
        var data = JsonSerializer.SerializeToElement(result.Data);
        Assert.Equal(JsonValueKind.Null, data.GetProperty("activityTree").ValueKind);
    }
```

(`BuildModel()` is the test class's existing model factory; `JsonValueKind` needs `using System.Text.Json;` — add it if the file lacks it.)

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/UiPath.Engineering.Mcp.Tools.Tests/UiPath.Engineering.Mcp.Tools.Tests.csproj --filter "FullyQualifiedName~ExplainWorkflowToolTests"`
Expected: FAIL — build error, `ExplainWorkflow` has no `includeActivityTree` parameter.

- [ ] **Step 3: Implement**

In `src/UiPath.Engineering.Mcp.Tools/ExplainWorkflowTool.cs`:

1. Add the parameter:

```csharp
        [Description("Workflow file to explain (file name, with or without .xaml/.cs, or a path).")] string workflowFile,
        [Description("When true, includes activityTree: the workflow's activities nested as a hierarchy with IDs and line numbers.")] bool includeActivityTree = false,
        CancellationToken cancellationToken = default) {
```

2. Pass it through the `ExplainXamlWorkflow(workflow, sw)` call → `ExplainXamlWorkflow(workflow, includeActivityTree, sw)` and extend that method. The tree property uses an explicit lowercase name (`activityTree`) because the tests serialize `Data` with default `System.Text.Json` options, which do not camel-case:

```csharp
    private static ToolResult ExplainXamlWorkflow(WorkflowModel workflow, bool includeActivityTree, Stopwatch sw) {
        var data = new {
            workflow.FileName,
            workflow.FilePath,
            workflow.IsMain,
            Arguments = workflow.Arguments.Select(a => new { a.Name, a.Direction, a.Type }).ToList(),
            Variables = workflow.Variables.Select(v => new { v.Name, v.Type, v.Scope }).ToList(),
            Activities = workflow.Activities.Select(a => new { a.DisplayName, a.Type, a.Depth }).ToList(),
            ExceptionHandlers = workflow.ExceptionHandlers.Select(e => new { e.HasGlobalHandler, e.CatchTypes }).ToList(),
            InvokeWorkflows = workflow.InvokeWorkflows.Select(i => new { i.DisplayName, i.TargetWorkflow }).ToList(),
            LogMessages = workflow.LogMessages.Select(l => new { l.DisplayName, l.Level, l.Message }).ToList(),
            workflow.HasParseError,
            workflow.ParseError,
            activityTree = includeActivityTree
                ? workflow.Activities.Where(a => a.ParentId is null).Select(ToTreeNode).ToList()
                : null
        };
        // ... warnings and ToolResults.Ok unchanged
    }

    private static object ToTreeNode(ActivityModel activity) => new {
        id = activity.Id,
        displayName = activity.DisplayName,
        type = activity.Type,
        line = activity.Line,
        children = activity.Children.Select(ToTreeNode).ToList()
    };
```

When the flag is false the property serializes as `activityTree: null` — additive and harmless; the second test asserts exactly that (below), so default-output expectations in pre-existing tests are untouched.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/UiPath.Engineering.Mcp.Tools.Tests/UiPath.Engineering.Mcp.Tools.Tests.csproj --filter "FullyQualifiedName~ExplainWorkflowToolTests"`
Expected: PASS — new tests plus all pre-existing explain tests.

- [ ] **Step 5: Commit**

```bash
git add src/UiPath.Engineering.Mcp.Tools/ExplainWorkflowTool.cs tests/UiPath.Engineering.Mcp.Tools.Tests/ExplainWorkflowToolTests.cs
git commit -m "feat(sp3): explain_workflow gains includeActivityTree hierarchy output"
```

---

### Task 11: Full-suite verification + acceptance smoke

**Files:** none (verification only).

- [ ] **Step 1: Run the entire test suite**

Run: `dotnet test UiPath.Engineering.Mcp.sln`
Expected: PASS across all three test projects (Core, Providers, Tools). Fix any regression by adjusting the additive change, never by deleting pre-existing assertions.

- [ ] **Step 2: Acceptance smoke against the real test project**

Start the server locally (`scripts/run-local.ps1`) and against `C:\Users\arauj\OneDrive\Documentos\UiPath\testProcess` verify the spec's acceptance criteria:

1. `find_activity` returns IDs and lines; a repeated call on an unchanged file returns identical IDs.
2. `get_workflow_dependencies` (no `workflowFile`) lists edges; per-workflow mode on `Main.xaml` shows callees with argument mappings, and on a child workflow shows its caller.
3. `edit_workflow_activity` with an `activityId` succeeds; reusing an ID captured before a structural edit fails with `ACTIVITY_ID_STALE`.
4. `explain_workflow` / `analyze_project` / `search_codebase` outputs are unchanged apart from the additive fields.

If the real project is unreachable from this machine, fall back to a scratch copy under the repo's temp folder and say so in the summary.

- [ ] **Step 3: Final commit (if any fixes were needed)**

```bash
git add -A
git commit -m "test(sp3): acceptance verification fixes"
```
