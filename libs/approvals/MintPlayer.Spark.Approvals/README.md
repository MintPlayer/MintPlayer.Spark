# MintPlayer.Spark.Approvals

Approvals for MintPlayer.Spark: **decision requests** (someone asks, the kind's deciders decide, the kind's handler
runs the decision) and **document proposals** (a configured type's create or edit waits for an approver, then
applies against the version it was made on). The guide is `docs/guide-approvals.md`.

```csharp
spark.AddAuthentication<SparkUser>();
spark.AddApprovals();   // Spark:Approvals
```

A kind is a marker type with deciders and a handler (discovered in an application, registered by a library):

```csharp
public sealed class RefundApproval : IApprovalKind { public static string Name => "shop.refund"; }

public sealed class RefundDeciders(IPermissionService permissions) : IApprovalDeciders<RefundApproval>
{
    public async ValueTask<bool> MayDecideAsync(ApprovalDeciderContext context)
        => await permissions.IsAllowedAsync("Refund", "Order");
}

public sealed class RefundHandler : IApprovalHandler<RefundApproval>
{
    public async Task ApproveAsync(ApprovalDecisionContext context)
        => (await context.Session.LoadAsync<Order>(context.Request.Subject.Id))!.Refunded = true;

    public Task RejectAsync(ApprovalDecisionContext context) => Task.CompletedTask;
}

await approvals.SubmitAsync<RefundApproval>(new ApprovalSubmission
{
    Subject = new ApprovalSubject("Order", order.Id!),
    Title = "Refund €40 on order 1042",
});
```

Document proposals need configuration and a right per type:

```json
"Spark": { "Approvals": { "Types": { "Company": { "Create": true, "Modify": true } } } }
```

```json
{ "resource": "ApproveProposal/Company", "groupId": "Approvers" }
```

The library ships the `ApprovalRequest` type (queries `approvals`, `pending-approvals`, `my-approval-requests`),
its decision actions, `/spark/approvals/*`, the notices (every `IApprovalNotifier`, or a MailManager mail) and a
sweep that expires overdue requests. Its rights name only `@authenticated`, so no application binds a slot for it.
