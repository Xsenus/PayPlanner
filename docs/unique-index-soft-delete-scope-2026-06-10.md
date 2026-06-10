# Unique index and soft-delete scope

Inventory date: 2026-06-10.

This document records the expected uniqueness rules that are currently enforced by the EF model. It does not introduce new database indexes; it documents the current contract and is guarded by `UniqueIndexBusinessScopeTests`.

| Area | Entity | Indexed fields | Unique | Soft-delete behavior | Business scope |
| --- | --- | --- | --- | --- | --- |
| Invoices | `Invoice` | `Number`, `Date` | No | Soft-deleted invoices do not affect uniqueness because the index is not unique. | Invoice numbers may repeat; lists use number/date indexes for search and filtering. |
| Acts | `Act` | `Number` | No | Soft-deleted acts do not affect uniqueness because the index is not unique. | Act numbers may repeat. |
| Contracts | `Contract` | `Number` | No | Soft-deleted contracts do not affect uniqueness because the index is not unique. | Contract numbers may repeat. |
| Cash orders | `CashOrder` | `Number` | Yes | Global unique index without `IsDeleted` filter; a soft-deleted cash order still reserves its number. | Cash order number is globally unique across all years, legal entities, and soft-delete state. |
| Cash inventories | `CashInventory` | `Number` | Yes | Global unique index without `IsDeleted` filter; a soft-deleted cash inventory still reserves its number. | Cash inventory number is globally unique across all years, accounts, and soft-delete state. |
| OKIN dictionary | `OkinDictionaryItem` | `Code` | Yes | Dictionary items are active/inactive, not soft-deleted in this scope. | Code is globally unique. |
| OKSO dictionary | `OksoDictionaryItem` | `Code` | Yes | Dictionary items are active/inactive, not soft-deleted in this scope. | Code is globally unique. |
| OKPDTR dictionary | `OkpdtrDictionaryItem` | `Code` | Yes | Dictionary items are active/inactive, not soft-deleted in this scope. | Code is globally unique. |

## Follow-up notes

- Changing cash order or cash inventory uniqueness to legal-entity/year scoped uniqueness would be a migration and business-rule change.
- Adding filtered unique indexes that ignore `IsDeleted` would allow number reuse after soft-delete, but that would change restore behavior and must be reviewed with users first.
- Current tests assert SQLite and PostgreSQL model metadata so provider-specific model drift is caught early.
