# Thin REFramework — `InvokeWorkflowFile` only

Copy into a UiPath project’s `docs/idioms/`. XAML may wire REFramework and invoke coded workflows. It must not contain Excel, HTTP, Mail, or UI activities.

`InvokeWorkflowFile` arguments are BCL and framework types only (`String`, `Boolean`, `Int32`, `Dictionary`, `IEnumerable`, `DataTable`, arrays). Never pass types defined in this automation. Never call coded-source methods from XAML.

`WorkflowFileName` is a plain relative path (`.cs` or `.xaml`), not an expression. When arguments are populated, use direct `InArgument` / `OutArgument` children — not an `scg:Dictionary` wrapper.

```xml
<ui:InvokeWorkflowFile DisplayName="Process invoice"
    WorkflowFileName="ProcessInvoice.cs" UnSafe="False">
  <ui:InvokeWorkflowFile.Arguments>
    <InArgument x:TypeArguments="x:String" x:Key="invoiceId">[in_TransactionItem.SpecificContent("InvoiceId").ToString]</InArgument>
    <OutArgument x:TypeArguments="x:String" x:Key="Output">[processedId]</OutArgument>
  </ui:InvokeWorkflowFile.Arguments>
</ui:InvokeWorkflowFile>
```

A coded workflow’s single return value maps to the `Output` argument. Keep `Main.xaml` / `Framework\*.xaml` as the shell; put the work in the invoked `.cs`.
