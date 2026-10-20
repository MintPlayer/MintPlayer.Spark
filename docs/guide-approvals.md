# Approvals: decision requests and document proposals

Someone asks, someone else decides, and the decision does something. `MintPlayer.Spark.Approvals` (alias
`approvals`) gives that one shape: a **decision request** of some **kind**, decided by the kind's **deciders**,
carried out by the kind's **handler**. **Document proposals** are one built-in kind on top: a save of a configured
type waits for an approver and is applied, later, against the version it was made on.

Decisions and evidence: [commerce_PRD.md](commerce_PRD.md) §4.13 (D9, D26) and
[commerce_research.md](commerce_research.md) §9.

```csharp
spark.AddAuthentication<SparkUser>();
spark.AddApprovals();       // Spark:Approvals; the identity provider and Organizations call it themselves
```

## 1. A kind

A kind is a marker type whose `Name` is stored on every request, so it never changes. Prefix it with your library
alias.

```csharp
public sealed class RefundApproval : IApprovalKind { public static string Name => "shop.refund"; }
```

It needs **deciders** and a **handler**. In an application both are discovered (`[SparkDiscoverable]`); a library
registers its own (`services.TryAddScoped<IApprovalDeciders<RefundApproval>, RefundDeciders>()`). Startup is refused
for deciders without a handler, a handler without deciders, two implementations of either, or two kinds with one name.

```csharp
public sealed class RefundDeciders(IPermissionService permissions) : IApprovalDeciders<RefundApproval>
{
    // The live gate, asked at decision time: a decider who lost the role cannot decide any more.
    public async ValueTask<bool> MayDecideAsync(ApprovalDeciderContext context)
        => await permissions.IsAllowedAsync("Refund", "Order", context.CancellationToken);

    // Who sees it as a decider without being listed: every refund request for refund holders.
    public async ValueTask<ApprovalScope> DecidesAllAsync(ClaimsPrincipal user, CancellationToken ct)
        => await permissions.IsAllowedAsync("Refund", "Order", ct) ? ApprovalScope.All : ApprovalScope.None;
}

public sealed class RefundHandler : IApprovalHandler<RefundApproval>
{
    public async Task ApproveAsync(ApprovalDecisionContext context)
    {
        var order = await context.Session.LoadAsync<Order>(context.Request.Subject.Id, context.CancellationToken);
        if (order is null) { context.MarkStale(); return; }
        order.Refunded = true;                      // stored with the request's new status, in one commit
    }

    public Task RejectAsync(ApprovalDecisionContext context) => Task.CompletedTask;   // context.Reason is set
}
```

| Deciders member | Answers |
|---|---|
| `MayDecideAsync` | the live gate for one request (required) |
| `GetDeciderIdsAsync` | the users to tell, who also see it in their inbox (an API's owners, an organization's admins) |
| `DecidesAllAsync` | requests a caller sees without being listed: `All` (a kind-wide population), `Types(...)` (per subject type) |

A handler runs **inside the decision's session**: store what it changes, never `SaveChanges`. The request's new
status commits in the same transaction, guarded by the version that was decided on, so of two deciders exactly one
commits; the other gets `NotPending`. `MarkStale()` turns the decision into `Stale` and commits nothing the handler
stored. `WithdrawAsync`, `ExpireAsync` and `RevokeAsync` are optional (revoking is refused unless implemented).

## 2. Raising and deciding

```csharp
await approvals.SubmitAsync<RefundApproval>(new ApprovalSubmission
{
    Subject = new ApprovalSubject("Order", order.Id!),        // a document, optionally a Key for one row of it
    Title = "Refund €40 on order 1042",
    Url = $"/po/order/{Uri.EscapeDataString(order.Id!)}",
    VisibleTo = [order.CustomerId!],                            // may see it, never decide it
});
```

- **One pending request per kind and subject.** A second submit answers the pending one (`Created: false`);
  `ReplacePending = true` withdraws it and raises a new one.
- **Inside a save:** `SubmitAsync<TKind>(session, submission)` stores the request in your session, so it commits with
  your writes or not at all (an interceptor raising several requests in one save). The deciders are told by the
  sweep once it committed.
- **Deciding:** `DecideAsync(id, Approve | Reject, reason)`, `WithdrawAsync(id)` (the requester only),
  `RevokeAsync(id, reason)` (a decider, on an approved request). A rejection and a revocation need a reason, which is
  stored on the request and sent to the requester.
- **Never self-approval.** The requester cannot decide their own request, whatever the gate answers.
- **One refusal.** A request that does not exist, that the caller may not see, or may not decide answers
  `NotFound`: over HTTP the uniform 401 (anonymous) or 404, so a request id is no oracle.
- **Closing from code:** `ClosePendingAsync<TKind>(session, subject)` withdraws pending requests without running the
  handler, when your code changes the subject itself (deleted, the row removed).

| Outcome | HTTP | Meaning |
|---|---|---|
| `Done` | 200 | the new status is in `status` |
| `NotFound` | 401/404 | the uniform refusal |
| `NotPending` | 409 | decided, withdrawn or expired already |
| `ReasonRequired` | 400 | a rejection or revocation without a reason |
| `Stale` | 409 | could not be applied as asked; the request is now `Stale` |
| `Unwritable` | 422 | a proposal sets attributes the approver may not write (still pending) |
| `Invalid` | 400 | a proposal failed validation (still pending) |
| `Cancelled` | 409 | an interceptor cancelled the apply (still pending) |

## 3. The `ApprovalRequest` type

The library ships the type, its queries and its actions. Its rights name only its own type and `@authenticated`, so
no application binds a slot for it (an unbound slot would refuse the startup of every app that gets the layer
through the identity provider). Every row is narrowed by `ApprovalRequestActions`' row rule: the requester, the
listed deciders, `VisibleTo`, and the `DecidesAllAsync` scopes.

| Query alias | Rows |
|---|---|
| `approvals` | every request the caller may see |
| `pending-approvals` | the inbox: pending requests someone else raised (its `totalItems` is the badge count) |
| `my-approval-requests` | the requests the caller raised |

The actions `ApprovalApprove`, `ApprovalReject` (asks for the reason), `ApprovalWithdraw` and `ApprovalRevoke` are
offered per row only where they would succeed. The page of a request is `/po/approval-request/{id}`. Add the queries
to your `programUnits.json` where your users should find them; the library ships no menu.

The request is its own audit trail: status, decider, time, reason, and a `History` of every change. It is kept for
`Spark:Approvals:Retention` (365 days) after it ends.

## 4. Notices, expiry and live updates

- **Notices.** A new request is told to its listed deciders; how it ended is told to its requester. Every
  `IApprovalNotifier` delivers (Notifications registers one: in-app, mail and push by preference). With none, the
  library mails through MailManager (`SparkApprovals/Submitted`, `SparkApprovals/Decided`, en + nl, deduplicated
  per request, event, status and user).
- **Expiry.** A request not decided within `DecideWithin` (default 30 days, or the submission's own) expires: the
  sweep (`Spark:Approvals:SweepSchedule`, every minute) runs the handler's `ExpireAsync` and tells the requester. Past
  its deadline a request is no longer decidable even before the sweep ran.
- **Live.** The type is `"live": true`, and each change is announced for the request document; each recipient also
  gets a private `approvals:inbox` frame (`{ requestId, status }`) for an inbox badge.
- **Write guard.** The `ISatelliteWriteGuard`s (Moderation's suspension and lock checks) are asked when a request
  about a document is raised or decided.

## 5. Document proposals

```json
"Spark": { "Approvals": { "Types": { "Company": { "Create": true, "Modify": true }, "Profession": { "Create": true } } } }
```

```json
{ "resource": "ApproveProposal/Company", "groupId": "Approvers" }
```

- A save of a listed type, by someone without `ApproveProposal/<Type>`, is diverted (`ISaveReplacement`,
  [guide-interceptors.md](guide-interceptors.md) §4a) after every gate it must pass: the caller gets **202
  "submitted for approval"** and the request id. An approver's own save is not diverted: their edit is its approval.
- **Pending creations are invisible.** The row does not exist until it is approved, so no query shows it.
- **Approving applies it** as the approver, through the same path as `/spark/po/update` (rights, row gates, write
  shield, validation, interceptors) with `Etag = base`: a row changed since answers **409** and the proposal becomes
  `Stale`. The request's status commits with the apply, or not at all.
- **Authorship.** The apply runs on the proposer's behalf (`SaveContext.OnBehalfOf`). History names the **proposer**
  as the author (`CreatedBy` on a creation, `ModifiedBy`), so the revision list shows who wrote the change; an entity
  that implements `IAuditApproved` also records the **approver** (`ApprovedBy`, `ApprovedAt`), which a direct save
  clears. Moderation makes the proposer the `AuthorId` of an approved creation, so reputation goes to the writer.
- **The diff.** ng-spark's approvals panel shows the proposal against the current row
  (`GET /spark/approvals/proposal?id=…`), attribute by attribute; a renderer can register a `diffComponent` for its
  own visual diff. A governed row with an edit pending carries `metadata.approval` for the "approval pending" badge.
- A type whose change vector moves on its own (a token refreshed hourly) would go stale every time: decide its
  changes with a kind of your own instead.

## 6. Configuration

| Key | Default | |
|---|---|---|
| `Spark:Approvals:Types` | none | the types whose saves are proposed (`Create`, `Modify`) |
| `Spark:Approvals:DecideWithin` | 30 days | the deadline of a request |
| `Spark:Approvals:Retention` | 365 days | how long an ended request is kept |
| `Spark:Approvals:SweepSchedule` | `* * * * *` | the expiry and notice sweep |
| `Spark:Approvals:Mail:Submitted` / `:Decided` | `SparkApprovals/…` | the fallback mail templates |

## 7. The client (ng-spark)

```ts
import { provideSparkApprovals, sparkApprovalsRenderers } from '@mintplayer/ng-spark/approvals';

providers: [provideSparkApprovals(), provideSparkAttributeRenderers([...sparkApprovalsRenderers])]
// shell: <spark-approvals-inbox-link linkClass="btn btn-sm btn-link" />
```

- **Decision panel** on an `ApprovalRequest`: the proposal diff (`<spark-po-diff>`; a creation lists the proposed
  values, a modification what changes, now → proposed), a warning when the row changed since (Approve is then
  disabled), and Approve / Reject (with a required reason) / Withdraw as the row's `disabledActions` allow. The
  buttons post to `/spark/approvals/decide` and `/withdraw` (`SparkApprovalsService`); a 400/409/422 shows its
  `outcome`, and the page reloads.
- **`approval-status`**: the status as a pill, one class per status (`spark-approval-status-pending`, …).
- **"Approval pending"** after a governed row's title, from `metadata.approval`, linking to the request
  (`SPARK_DETAIL_TITLE_BADGES` in `@mintplayer/ng-spark/panels`).
- **A diverted creation opens its request** (`SPARK_SAVE_SUBMITTED_HANDLERS`): the object does not exist yet, so its
  own page would be a 404. A diverted edit returns to the detail page, which shows the badge.
- **Inbox link**: the `pending-approvals` count over the live socket, re-read on every private `approvals:inbox`
  frame; hidden at zero.
