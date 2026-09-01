# Billing cost attribution

The default `OperationCostAttribution` is deliberately a DBU proxy. Use the billing reader only
when the application identity can read the Databricks billing system tables and the product needs
currency-denominated effective list cost.

## Prerequisites

- The Databricks identity registered by `AddLakeWrightDatabricks` can use
  `system.billing` and select from `system.billing.usage` and
  `system.billing.list_prices`.
- `DatabricksBilling:WorkspaceId` is the workspace whose Lakeflow run ids are stored in this
  application's `operations.ExternalId` column.
- Operation processing uses the LakeWright worker, so `ExternalId` is the Lakeflow
  `job_run_id`. Statement ids and job ids are different identifiers and are not accepted.

System billing rows are account-wide and typically arrive after the workload finishes. The reader
always filters `workspace_id`, job run id, timestamp and `usage_date`; a report can therefore be
empty while recent records are still being delivered.

## Registration

```csharp
builder.Services.AddLakeWright(builder.Configuration);
builder.Services.AddLakeWrightDatabricks(builder.Configuration);
builder.Services.AddLakeWrightBillingCostAttribution(builder.Configuration);
```

```json
{
  "Databricks": {
    "WorkspaceUrl": "https://adb-....azuredatabricks.net",
    "WarehouseId": "..."
  },
  "DatabricksBilling": {
    "WorkspaceId": "...",
    "PollIntervalMilliseconds": 250,
    "PollingTimeoutSeconds": 120,
    "SubmissionWaitTimeoutSeconds": 30,
    "MaxConcurrentStatements": 4,
    "MaxOutstandingStatements": 32
  }
}
```

`AddLakeWrightBillingCostAttribution` replaces the proxy registration. Keep
`AddLakeWrightCostAttribution` instead in environments without the grants.

The response retains the existing DBU fields. `EstimatedListCost` is a collection of
`CurrencyAmount` values on both the summary and each operation-kind row. The amount uses
`pricing.effective_list.default`; it does not include a private negotiated discount and must not be
presented as an invoice total. `ElapsedSeconds` is zero for billing rows because the billing table
reports quantities and usage intervals, not the operation wall-clock value used by the proxy.

Usage rows that cross either report-window or price-validity boundaries are prorated by their
overlap. The same prorated quantity feeds both DBUs and effective list cost, and a price change
inside one usage row contributes one segment at each effective price. The endpoint never adds
amounts in unlike currencies when ordering operation kinds; it orders by DBUs, then kind.

One report is limited to 31 days, may end at most one day in the future, contains at most 500
distinct tenant-owned job runs, and issues one billing-system query. These limits are enforced by
the public service and reader as well as the HTTP endpoint, so direct DI consumers cannot bypass
the scan bounds.
HTTP 422 with code `REPORT_TOO_LARGE` means the caller must narrow the window. This prevents a
high-volume tenant from turning one request into repeated scans of the account-wide billing table.
A statement that remains pending past `PollingTimeoutSeconds` is cancelled best-effort and returns
the transient code `POLL_TIMEOUT`.

The billing reader is shared across request scopes. At most `MaxConcurrentStatements` statement
lifecycles run at once, and active plus queued work cannot exceed `MaxOutstandingStatements`.
Additional requests fail with transient code `BILLING_BUSY`; the HTTP endpoint maps it to 503.
Choose bounds that fit the configured warehouse and apply normal edge rate limiting as well.
After statement creation begins, caller cancellation holds its slot until Databricks answers; if
the returned statement is still active, the reader cancels it before surfacing cancellation. The
same overall timeout bounds both initial submission and polling. Billing submissions use
server-side `on_wait_timeout=CANCEL`. If the create response is lost and no statement id is
available, local admission remains held until that server cancellation deadline before the safe
transient code `STATEMENT_CREATE_UNCERTAIN` is returned.

## Live verification

Run this only in the non-production workspace whose id is configured above.

1. Start one known LakeWright operation and wait for its `ExternalId` job run to finish.
2. Wait for the corresponding `system.billing.usage` record to arrive. Databricks documents a
   typical delay of up to 12 hours for original records.
3. Query the cost endpoint for a window that contains the run. Confirm `Source` is `Billing`, the
   run contributes once to `Operations`, and `EstimatedListCost` carries the expected currency.
4. Compare the returned DBUs and effective list cost with a direct, read-only query over the same
   workspace id, job run id and window.
5. Remove the system-table grants from a disposable verification principal and confirm the endpoint
   returns HTTP 502 with `PERMISSION_DENIED`; restore the grant afterwards.

No test data needs to be written to the system tables. Do not use a production workspace merely to
obtain a billing row.
