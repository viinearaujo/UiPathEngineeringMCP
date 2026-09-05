# Coded workflow — `try` / `Log` on the entry method

Copy into a UiPath project’s `docs/idioms/`. Business logic lives in a `[Workflow]` method, not in XAML.

One class per file; class name equals file name. Wrap the entry method in `try` / `catch`. Log start, success, and failures. Re-throw with `throw;` (bare) so the stack is preserved. Use `UiPath.Core.BusinessRuleException` for data that must not be retried.

```csharp
using System;
using UiPath.CodedWorkflows;
using UiPath.Core;

namespace SampleProject
{
    public class ProcessInvoice : CodedWorkflow
    {
        [Workflow]
        public string Execute(string invoiceId)
        {
            try
            {
                Log($"Processing invoice {invoiceId}");
                if (string.IsNullOrWhiteSpace(invoiceId))
                {
                    throw new BusinessRuleException("Invoice id is empty");
                }

                // Excel, HTTP, mail, and UI calls go here — not in XAML.

                Log($"Invoice {invoiceId} posted");
                return invoiceId;
            }
            catch (BusinessRuleException)
            {
                Log("business error", LogLevel.Warn);
                throw;
            }
            catch (Exception ex)
            {
                Log(ex.ToString(), LogLevel.Error);
                throw;
            }
        }
    }
}
```
