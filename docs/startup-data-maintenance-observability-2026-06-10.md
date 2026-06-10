# Startup data maintenance observability

Inventory date: 2026-06-10.

The backend startup pipeline runs database maintenance before controllers and hosted services accept traffic. Each step is logged with start, success duration, and failure duration under the `StartupDataMaintenance` logger.

| Step | SQLite operation | PostgreSQL operation | Failure behavior |
| --- | --- | --- | --- |
| Database migrations | `PaymentContext.Database.MigrateAsync()` | `PostgreSqlPaymentContext.Database.MigrateAsync()` | Startup fails fast and logs the exception. |
| Seed data | `SeedDataService.SeedAsync(...)` | `SeedDataService.SeedAsync(...)` | Startup fails fast and logs the exception. |
| App release sync | `AppReleaseSeedService.SyncAsync(...)` | `AppReleaseSeedService.SyncAsync(...)` | Startup fails fast and logs the exception. |
| Payment remainder transfer backfill | `PaymentRemainderTransferBackfillService.EnsureLegacyLinksAsync(...)` | `PaymentRemainderTransferBackfillService.EnsureLegacyLinksAsync(...)` | Startup fails fast and logs the exception. |
| Task contractor payment backfill | `TaskContractorPaymentBackfillService.EnsureGeneratedPaymentDetailsAsync(...)` | `TaskContractorPaymentBackfillService.EnsureGeneratedPaymentDetailsAsync(...)` | Startup fails fast and logs the exception. |

## Operational notes

- A successful startup log includes `Startup data step <name> completed in <ms> ms`.
- A failed startup log includes `Startup data step <name> failed after <ms> ms` and the original exception.
- Rollback remains deployment-specific: stop the service, restore the database backup or revert the failed migration/backfill input, then restart after the failing step is understood.
- The startup order is unchanged: migrations, seed data, release sync, payment remainder transfer backfill, task contractor payment backfill.
