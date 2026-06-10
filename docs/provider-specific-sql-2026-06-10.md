# Provider-specific SQL

Inventory date: 2026-06-10.

The only current provider-specific SQL found in application code is the invoice aggregate lock in `InvoicesController.LockInvoiceAggregateAsync`.

| Area | SQL | Provider guard | SQLite behavior | PostgreSQL behavior |
| --- | --- | --- | --- | --- |
| Invoice aggregate lock | `SELECT 1 FROM "Payments" WHERE "InvoiceId" = ... FOR UPDATE` | `_db.Database.IsNpgsql()` | Method returns without raw SQL because SQLite does not support `FOR UPDATE`. | Payment rows for the invoice aggregate are locked inside the surrounding transaction. |

## Guardrails

- `ProviderSpecificSqlTests` asserts that the `FOR UPDATE` statement remains inside the PostgreSQL guard.
- The same test scans backend application code for additional `FOR UPDATE` usage outside migrations/build outputs.
- If provider-specific SQL is added later, it must either be guarded by provider checks or covered by provider-specific tests for SQLite and PostgreSQL behavior.
