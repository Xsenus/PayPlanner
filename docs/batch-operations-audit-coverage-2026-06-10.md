# Batch operations and audit coverage

Inventory date: 2026-06-10.

EF `ExecuteUpdateAsync` and `ExecuteDeleteAsync` bypass the change tracker. That means `PaymentContext` object audit hooks do not see per-entity changes from these operations. This document records each current usage and why it is acceptable.

| Location | Operation | Data affected | Audit behavior | Rationale |
| --- | --- | --- | --- | --- |
| `backend/Services/AuthService.cs:266` | `ExecuteUpdateAsync` | Current user's login/activity summary fields | No object audit event; login itself is represented by auth/user activity flow. | Avoids stale tracked user state and updates a small set of technical presence fields atomically. |
| `backend/Controllers/UserActivityController.cs:403` | `ExecuteUpdateAsync` | Current user's last activity metadata | No object audit event; the user activity log row is the audit trail. | Records lightweight activity metadata without loading the full user aggregate on every request. |
| `backend/Controllers/UserActivityController.cs:525` | `ExecuteDeleteAsync` | User activity log cleanup requested by admin | No per-row object audit; controller logs cleanup count and returns maintenance result. | The operation intentionally clears technical activity logs in bulk. |
| `backend/Controllers/LegalEntitiesController.cs:961` | `ExecuteDeleteAsync` | Legal entity address records before replacing the address collection | No per-address object audit; legal entity save flow records the visible entity change. | Address records are treated as replaceable child rows of the legal entity form. |
| `backend/Services/UserActivity/UserActivityRetentionService.cs:109` | `ExecuteDeleteAsync` | Expired user activity log rows | No per-row object audit; service logs deleted count and retention cutoff. | Retention cleanup removes technical logs in bulk. |
| `backend/Services/Notifications/NotificationDeliveryRetentionService.cs:152` | `ExecuteDeleteAsync` | Expired notification delivery technical rows | No per-row object audit; service records system job state and logs deleted count. | Retention cleanup removes delivery diagnostics in bulk. |
| `backend/Services/CalendarSyncLogRetentionService.cs:198` | `ExecuteDeleteAsync` | Expired Google calendar synchronization technical rows | No per-row object audit; service records the provider-specific and total deleted counts in the system job state. | Retention cleanup removes technical synchronization diagnostics in bounded chunks. |
| `backend/Services/CalendarSyncLogRetentionService.cs:209` | `ExecuteDeleteAsync` | Expired Yandex calendar synchronization technical rows | No per-row object audit; service records the provider-specific and total deleted counts in the system job state. | Retention cleanup removes technical synchronization diagnostics in bounded chunks. |

## Guardrails

- `BatchOperationAuditCoverageTests` scans backend application code and fails when a new `ExecuteUpdateAsync` or `ExecuteDeleteAsync` call is added without documenting it here.
- New batch operations must explicitly state whether object audit is intentionally skipped, replaced by another audit trail, or needs a compensating audit event.
