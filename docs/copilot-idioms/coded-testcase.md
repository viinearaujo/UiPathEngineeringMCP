# Coded test case — `[TestCase]`

Copy into a UiPath project’s `docs/idioms/`. Add with `add_coded_workflow` `kind=test` (Process projects default to `Tests\`). Registers `fileInfoCollection`, never `entryPoints`.

Same `CodedWorkflow` base as a workflow; the entry method is `[TestCase]`, not `[Workflow]`. Arrange / Act / Assert. Call the workflow under test with `workflows.Name(...)`. Assert with `testing.VerifyExpression` / `testing.VerifyAreEqual`.

```csharp
using System;
using UiPath.CodedWorkflows;

namespace SampleProject
{
    public class ProcessInvoiceTests : CodedWorkflow
    {
        [TestCase]
        public void Execute()
        {
            // Arrange
            string invoiceId = "INV-001";
            Log($"Testing ProcessInvoice for {invoiceId}");

            // Act
            var result = workflows.ProcessInvoice(invoiceId: invoiceId);

            // Assert
            testing.VerifyExpression(!string.IsNullOrEmpty(result), "ProcessInvoice should return an id");
            testing.VerifyAreEqual("INV-001", result, "Invoice id should round-trip");
        }
    }
}
```
